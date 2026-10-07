(function () {
    'use strict';

    function appPathBase() {
        const value = typeof window.__CF_BASE__ === 'string' ? window.__CF_BASE__ : '';
        if (!value || value === '/') {
            return '';
        }
        return value.endsWith('/') ? value.slice(0, -1) : value;
    }

    function withAppBase(path) {
        const normalized = path.startsWith('/') ? path : '/' + path;
        return appPathBase() + normalized;
    }

    const CONTENT_TYPES = ['TV Show', 'Movie', 'Music Video', 'Music', 'Weather', 'News'];
    const CANDIDATE_KINDS = ['Jellyfin Item', 'Collection', 'Filter Query', 'Playlist / List'];
    const SLOT_CANDIDATE_KIND_VALUES = {
        0: 0, 1: 1, 2: 2, 3: 3,
        jellyfinItem: 0, JellyfinItem: 0,
        collection: 1, Collection: 1,
        filterQuery: 2, FilterQuery: 2,
        playlist: 3, Playlist: 3
    };
    const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
    const CONTENT_TYPE_VALUES = {
        0: 0, 1: 1, 2: 2, 3: 3, 4: 4, 5: 5,
        tvShow: 0, movie: 1, musicVideo: 2, music: 3, weather: 4, news: 5,
        TvShow: 0, Movie: 1, MusicVideo: 2, Music: 3, Weather: 4, News: 5
    };
    const ASPECT_RATIO_VALUES = {
        0: 0, 1: 1,
        sixteenNine: 0, fourThree: 1,
        SixteenNine: 0, FourThree: 1
    };
    const BUG_PLACEMENT_VALUES = {
        0: 0, 1: 1, 2: 2, 3: 3, 4: 4, 5: 5, 5: 5,
        auto: 0, topLeft: 1, topRight: 2, bottomLeft: 3, bottomRight: 4, none: 5,
        Auto: 0, TopLeft: 1, TopRight: 2, BottomLeft: 3, BottomRight: 4, None: 5
    };
    const DEFAULT_BUG_PLACEMENT = BUG_PLACEMENT_VALUES.BottomRight;

    let channels = [];
    let logoSets = [];
    let selectedChannelId = null;
    let editingChannelId = null;
    let lineupSlots = [];
    let lineupOverrides = [];
    let selectedLineupOverrideId = null;
    let lineupIsWeather = false;
    let itemTitleCache = {};
    let itemMetaCache = {};
    let channelFilter = '';
    let channelOnAir = {};
    let onAirRefreshTimer = null;
    let channelPresets = [];
    let presetNumberingMode = 1;
    let configPage = null;
    let aiSettings = null;
    let aiProviderDraft = null;
    let aiVisibleProviderId = 0;
    let aiChannels = [];
    let aiPlayoutTemplates = [];
    let aiPage = 'lineups';
    let primetimeShows = [];
    let primetimeSlots = [];
    let primetimeSelectedSlot = 36;
    let primetimeSearch = '';
    let aiPreview = null;
    let weatherDockerStatus = null;
    let newsFeeds = [];
    let finTvLists = [];
    let listNameCache = {};
    let specialPresentations = [];
    let specialChannelId = null;
    let commercialSearchPlaylists = [];
    let selectedSearchPlaylistId = null;
    const searchPlaylistPullsInFlight = new Set();
    let searchPlaylistSaveBusy = false;
    let deepEditingChannelId = null;
    let deepChannelPlaylistIds = [];
    let commercialsPage = 'library';
    let mapChannelId = null;
    let mapChannelPlaylistIds = [];
    let guideData = null;
    let guideFromIso = null;
    let guideDateFilter = null;
    let guideTimer = null;
    let guideScrollToken = 0;
    let scheduleTimeZone = null;
    let catalogSyncPollTimer = null;
    let catalogSyncHideTimer = null;
    let catalogSyncKind = null;
    let catalogSyncSeenRunning = false;
    let catalogSyncClosed = false;
    let catalogSyncWasRunning = false;
    let catalogSyncExpectingStart = 0;
    const CATALOG_SYNC_SNAP_KEY = 'channelflow.catalogSync.snap';
    let libraryTaskKind = null;
    let libraryTaskSeen = {};
    let libraryTaskClosedKinds = {};
    let libraryTaskWasRunning = {};
    let libraryTaskExpecting = {};
    let libraryTaskHideTimers = {};
    const LIBRARY_TASK_SNAP_KEY = 'channelflow.libraryTask.snap';
    let appClockTimer = null;
    const GUIDE_PX_PER_MIN = 4;
    const GUIDE_CHANNEL_COL = 168;

    function $(id) {
        if (configPage) {
            const found = configPage.querySelector('#' + id);
            if (found) {
                return found;
            }
        }

        return document.getElementById(id);
    }

    function q(selector) {
        return configPage ? configPage.querySelector(selector) : document.querySelector(selector);
    }

    function qa(selector) {
        return configPage ? configPage.querySelectorAll(selector) : document.querySelectorAll(selector);
    }

    function resolveUrl(path) {
        const normalized = path.startsWith('/') ? path.slice(1) : path;
        if (false) {
            return ApiClient.getUrl(normalized);
        }
        return withAppBase('/' + normalized.replace(/^(?:ChannelFlow|FinTV)\/api/, 'api'));
    }

    function parseErrorMessage(message) {
        if (!message) {
            return 'Request failed';
        }

        try {
            const parsed = JSON.parse(message);
            const genericTitles = new Set([
                'error processing request',
                'an error occurred while processing your request.',
                'an error occurred while processing your request',
                'bad request',
                'internal server error'
            ]);

            if (parsed.detail) {
                return parsed.detail;
            }

            if (parsed.message) {
                return parsed.message;
            }

            if (parsed.title && !genericTitles.has(String(parsed.title).trim().toLowerCase())) {
                return parsed.title;
            }

            if (parsed.errors) {
                return Object.values(parsed.errors).flat().join(' ');
            }

            if (parsed.title) {
                return parsed.title;
            }
        } catch (ignore) {
            // Keep raw response text.
        }

        return message;
    }

    async function readApiFailure(err) {
        if (err instanceof Response) {
            const text = await err.text();
            throw new Error(parseErrorMessage(text || err.statusText));
        }

        if (err && typeof err.responseText === 'string' && err.responseText) {
            throw new Error(parseErrorMessage(err.responseText));
        }

        const message = (err && err.message) || 'Request failed';
        if (isNetworkError(message)) {
            throw new Error('Jellyfin server is unreachable');
        }

        throw new Error(message);
    }

    function isNetworkError(err) {
        const message = String((err && err.message) || err || '').toLowerCase();
        return message.includes('failed to fetch')
            || message.includes('network error')
            || message.includes('connection refused')
            || message.includes('load failed')
            || message.includes('jellyfin server is unreachable');
    }

    function reportApiError(err, fallbackMessage) {
        if (isNetworkError(err)) {
            stopOnAirPolling();
            toast('Jellyfin server is unreachable. Restart the server and refresh this page.', 'error');
            return;
        }

        toast((err && err.message) || fallbackMessage || 'Request failed', 'error');
    }

    function normalizeApiValue(value) {
        if (Array.isArray(value)) {
            return value.map(normalizeApiValue);
        }

        if (!value || typeof value !== 'object') {
            return value;
        }

        const normalized = {};
        Object.keys(value).forEach((key) => {
            const camelKey = key.length ? key.charAt(0).toLowerCase() + key.slice(1) : key;
            normalized[camelKey] = normalizeApiValue(value[key]);
        });
        return normalized;
    }

    function normalizeApiResponse(value) {
        return value == null ? value : normalizeApiValue(value);
    }

    function parseApiJsonBody(text) {
        if (text == null || text === '') {
            return null;
        }

        if (typeof text === 'object') {
            return text;
        }

        try {
            return JSON.parse(text);
        } catch (err) {
            throw new Error('Invalid JSON response from server');
        }
    }

    function resolveEnumValue(map, value, fallback) {
        if (value == null || value === '') {
            return fallback;
        }

        if (typeof value === 'number' && Number.isFinite(value)) {
            return value;
        }

        const key = String(value);
        if (Object.prototype.hasOwnProperty.call(map, key)) {
            return map[key];
        }

        const parsed = parseInt(key, 10);
        return Number.isFinite(parsed) ? parsed : fallback;
    }

    function contentTypeLabel(value) {
        const index = resolveEnumValue(CONTENT_TYPE_VALUES, value, null);
        return index == null ? String(value) : (CONTENT_TYPES[index] || String(value));
    }

    function normalizeChannel(channel) {
        if (!channel) {
            return channel;
        }

        channel.contentType = resolveEnumValue(CONTENT_TYPE_VALUES, channel.contentType, 0);
        channel.aspectRatio = resolveEnumValue(ASPECT_RATIO_VALUES, channel.aspectRatio, 0);
        channel.bugPlacement = resolveEnumValue(BUG_PLACEMENT_VALUES, channel.bugPlacement, DEFAULT_BUG_PLACEMENT);
        channel.commercialSearchPlaylistIds = Array.isArray(channel.commercialSearchPlaylistIds)
            ? channel.commercialSearchPlaylistIds
            : [];
        return channel;
    }

    function setSelectEnum(selectId, map, value, fallback) {
        const select = $(selectId);
        if (!select) {
            return fallback;
        }

        const resolved = resolveEnumValue(map, value, fallback);
        select.value = String(resolved);
        return resolved;
    }

    function readSelectEnum(selectId, map, fallback) {
        const select = $(selectId);
        if (!select) {
            return fallback;
        }

        return resolveEnumValue(map, select.value, fallback);
    }

    function existingChannelDeepFields(channel) {
        const c = channel || (editingChannelId ? channels.find((x) => x.id === editingChannelId) : null);
        return {
            FilterJson: c?.filterJson ?? null,
            CatalogMode: c?.catalogMode ?? null,
            AiFineTunePrompt: c?.aiFineTunePrompt ?? null,
            CommercialPresetId: c?.commercialPresetId ?? null,
            CommercialSearchPlaylistIds: Array.isArray(c?.commercialSearchPlaylistIds)
                ? c.commercialSearchPlaylistIds.slice()
                : []
        };
    }

    function buildChannelPayload(form) {
        return Object.assign(existingChannelDeepFields(form.channel), {
            Number: form.number,
            Name: form.name,
            ContentType: form.contentType,
            AspectRatio: form.aspectRatio,
            ScanlinesEnabled: false,
            BugPlacement: form.bugPlacement,
            AudioLanguage: form.audioLanguage,
            LogoSetId: form.logoSetId,
            LogoFileName: form.logoFileName,
            WeatherLocationQuery: form.weatherLocationQuery,
            Enabled: form.enabled
        }, form.deep || {});
    }

    function isEmptyApiBody(data) {
        return data == null || data === '' || (typeof data === 'string' && !data.trim());
    }

    function isEmptyApiResponseError(err) {
        const message = String((err && err.message) || '');
        const status = err && err.status;
        const responseText = err && typeof err.responseText === 'string' ? err.responseText : '';
        return status === 204
            || ((message === 'parsererror' || message.includes('Unexpected end of JSON input'))
                && !responseText);
    }

    function normalizeApiResponseData(data) {
        if (isEmptyApiBody(data)) {
            return null;
        }

        if (typeof data === 'object') {
            return normalizeApiResponse(data);
        }

        return normalizeApiResponse(parseApiJsonBody(data));
    }

    function ajaxViaApiClient(ajaxOptions) {
        return new Promise((resolve, reject) => {
            let settled = false;
            const finish = (action, value) => {
                if (settled) {
                    return;
                }

                settled = true;
                action(value);
            };

            const handleSuccess = (data, statusCode) => {
                if (statusCode === 204 || isEmptyApiBody(data)) {
                    finish(resolve, null);
                    return;
                }

                try {
                    finish(resolve, normalizeApiResponseData(data));
                } catch (parseErr) {
                    if (isEmptyApiBody(data)) {
                        finish(resolve, null);
                        return;
                    }

                    finish(reject, parseErr);
                }
            };

            const handleFailure = async (err) => {
                if (settled) {
                    return;
                }

                if (err instanceof Response) {
                    if (err.status === 204 || err.status === 205) {
                        finish(resolve, null);
                        return;
                    }

                    try {
                        await readApiFailure(err);
                    } catch (parsedErr) {
                        finish(reject, parsedErr instanceof Error ? parsedErr : new Error(String(parsedErr)));
                    }

                    return;
                }

                if (isEmptyApiResponseError(err)) {
                    finish(resolve, null);
                    return;
                }

                const message = String((err && err.message) || err || '');
                if (message.includes('Unexpected end of JSON input') || message === 'parsererror') {
                    finish(resolve, null);
                    return;
                }

                try {
                    await readApiFailure(err);
                } catch (parsedErr) {
                    finish(reject, parsedErr instanceof Error ? parsedErr : new Error(String(parsedErr)));
                }
            };

            ajaxOptions.success = (data, _textStatus, xhr) => {
                handleSuccess(data, xhr && xhr.status);
            };

            ajaxOptions.error = (xhr, textStatus) => {
                if (isEmptyApiResponseError({ status: xhr.status, message: textStatus, responseText: xhr.responseText })) {
                    finish(resolve, null);
                    return;
                }

                void readApiFailure({ responseText: xhr.responseText, message: textStatus, status: xhr.status })
                    .catch((err) => finish(reject, err instanceof Error ? err : new Error(String(err))));
            };

            let ajaxResult;
            try {
                ajaxResult = ApiClient.ajax(ajaxOptions);
            } catch (syncErr) {
                finish(reject, syncErr instanceof Error ? syncErr : new Error(String(syncErr)));
                return;
            }

            if (ajaxResult && typeof ajaxResult.then === 'function') {
                ajaxResult.then((data) => {
                    if (settled) {
                        return;
                    }

                    handleSuccess(data);
                }).catch((err) => {
                    void handleFailure(err);
                });
            }
        });
    }

    function api(path, options) {
        options = options || {};
        const url = resolveUrl('/api' + (path.startsWith('/') ? path : '/' + path));
        const method = options.method || 'GET';
        const body = options.body == null
            ? undefined
            : (typeof options.body === 'string' ? options.body : JSON.stringify(options.body));

        if (false && typeof ApiClient !== 'undefined' && typeof ApiClient.ajax === 'function') {
            const ajaxOptions = {
                url: url,
                type: method,
                dataType: 'text',
                headers: {
                    accept: 'application/json'
                }
            };

            if (body) {
                ajaxOptions.contentType = 'application/json';
                ajaxOptions.data = body;
            }

            return ajaxViaApiClient(ajaxOptions);
        }

        const fetchOptions = {
            method: method,
            credentials: 'same-origin',
            cache: 'no-store',
            headers: {
                accept: 'application/json'
            }
        };

        if (body) {
            fetchOptions.headers['Content-Type'] = 'application/json';
            fetchOptions.body = body;
        }

        if (typeof ApiClient !== 'undefined' && typeof ApiClient.accessToken === 'function') {
            const token = ApiClient.accessToken();
            if (token) {
                fetchOptions.headers['X-Emby-Token'] = token;
            }
        }

        return fetch(url, fetchOptions).then(async (res) => {
            if (res.status === 401) { window.dispatchEvent(new Event('channelflow-auth-required')); throw new Error('Sign in required'); }
            if (!res.ok) {
                const text = await res.text();
                throw new Error(parseErrorMessage(text || res.statusText));
            }

            if (res.status === 204 || res.status === 205) {
                return null;
            }

            const text = await res.text();
            if (isEmptyApiBody(text)) {
                return null;
            }

            return normalizeApiResponse(parseApiJsonBody(text));
        }).catch((err) => {
            if (isNetworkError(err)) {
                throw new Error('Jellyfin server is unreachable');
            }

            throw err;
        });
    }

    async function apiForm(path, formData, method) {
        const url = resolveUrl('/api' + (path.startsWith('/') ? path : '/' + path));
        const httpMethod = method || 'POST';

        if (false && typeof ApiClient !== 'undefined' && typeof ApiClient.ajax === 'function') {
            return ajaxViaApiClient({
                url: url,
                type: httpMethod,
                data: formData,
                contentType: false,
                processData: false,
                dataType: 'text',
                headers: {
                    accept: 'application/json'
                }
            });
        }

        const headers = {
            accept: 'application/json'
        };
        if (typeof ApiClient !== 'undefined' && typeof ApiClient.accessToken === 'function') {
            const token = ApiClient.accessToken();
            if (token) {
                headers['X-Emby-Token'] = token;
            }
        }

        const fetchOptions = {
            method: httpMethod,
            credentials: 'same-origin',
            headers: headers,
            body: formData
        };

        try {
            const res = await fetch(url, fetchOptions);
            if (!res.ok) {
                const text = await res.text();
                throw new Error(parseErrorMessage(text || res.statusText));
            }

            if (res.status === 204) {
                return null;
            }

            const text = await res.text();
            return normalizeApiResponse(parseApiJsonBody(text));
        } catch (err) {
            if (isNetworkError(err)) {
                throw new Error('Jellyfin server is unreachable');
            }

            throw err;
        }
    }

    function toast(message, type) {
        const container = $('toast-container');
        if (!container) {
            return;
        }

        const el = document.createElement('div');
        el.className = 'toast' + (type ? ' ' + type : '');
        el.textContent = message;
        container.appendChild(el);
        setTimeout(() => el.remove(), 4200);
    }

    function slotTime(index) {
        const h = Math.floor(index / 2);
        const m = index % 2 ? '30' : '00';
        const h12 = ((h + 11) % 12) + 1;
        const ampm = h < 12 ? 'AM' : 'PM';
        return `${h12}:${m} ${ampm}`;
    }

    function formatChannelNumber(number) {
        const value = Math.round(Number(number) * 10) / 10;
        const major = Math.trunc(value);
        const minor = Math.round((value - major) * 10);
        return minor === 0 ? String(major) : `${major}.${minor}`;
    }

    function parseChannelNumber(raw) {
        const text = String(raw).trim();
        if (!/^\d+(\.\d)?$/.test(text)) {
            throw new Error('Channel number must be at least 1 with at most one decimal digit (.0 through .9).');
        }
        const value = Number(text);
        const major = Math.trunc(value);
        const minor = Math.round((value - major) * 10);
        if (value < 1 || minor < 0 || minor > 9) {
            throw new Error('Channel sub-number must be .0 through .9.');
        }
        return value;
    }

    function parseWeatherLocationQuery(value) {
        const location = String(value ?? '').trim();
        if (!location) {
            return null;
        }

        const zip = location.match(/\b(\d{5})(?:-\d{4})?\b/);
        return zip ? zip[1] : location;
    }

    function splitWeatherPermalink(permalink) {
        let url;
        try {
            url = new URL(String(permalink ?? '').trim());
        } catch (ignore) {
            throw new Error('Invalid WeatherStar permalink URL.');
        }

        const params = new URLSearchParams(url.search);
        ['latLonQuery', 'latLon', 'txtLocation', 'lat', 'lon', 'kiosk', 'wide'].forEach((key) => params.delete(key));
        const pathname = url.pathname.endsWith('/') && url.pathname.length > 1
            ? url.pathname.slice(0, -1)
            : url.pathname;

        return {
            baseUrl: `${url.origin}${pathname}`,
            query: params.toString()
        };
    }

    function escapeHtml(text) {
        return String(text)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    function appTimeZone() {
        return scheduleTimeZone || (guideData && guideData.timeZone) || undefined;
    }

    function setScheduleTimeZone(id) {
        if (id) {
            scheduleTimeZone = id;
        }
        startAppClock();
    }

    function startAppClock() {
        const el = document.getElementById('app-clock');
        if (!el) {
            return;
        }

        const tick = () => {
            try {
                const tz = appTimeZone();
                el.textContent = new Date().toLocaleString([], {
                    weekday: 'short',
                    month: 'short',
                    day: 'numeric',
                    hour: 'numeric',
                    minute: '2-digit',
                    second: '2-digit',
                    timeZone: tz,
                    timeZoneName: tz ? 'short' : undefined
                });
            } catch (ignore) {
                el.textContent = new Date().toLocaleString();
            }
        };

        tick();
        if (appClockTimer) {
            clearInterval(appClockTimer);
        }
        appClockTimer = setInterval(tick, 1000);
    }

    function todayIsoDate() {
        try {
            return new Intl.DateTimeFormat('en-CA', {
                timeZone: appTimeZone(),
                year: 'numeric',
                month: '2-digit',
                day: '2-digit'
            }).format(new Date());
        } catch (ignore) {
            return new Date().toISOString().slice(0, 10);
        }
    }

    function openModal(title, bodyHtml, footerHtml) {
        $('modal-title').textContent = title;
        $('modal-body').innerHTML = bodyHtml;
        $('modal-footer').innerHTML = footerHtml || '';
        $('modal-backdrop').classList.remove('hidden');
        decorateCheckboxes();
    }

    function closeModal() {
        $('modal-backdrop').classList.add('hidden');
        $('modal-body').innerHTML = '';
        $('modal-footer').innerHTML = '';
    }

    async function lookupItemTitles(ids) {
        const missing = ids.filter((id) => id && !itemTitleCache[id]);
        if (missing.length === 0) return;
        const results = await api('/catalog/lookup', { method: 'POST', body: JSON.stringify({ ids: missing }) });
        (results || []).forEach((item) => {
            itemTitleCache[item.id] = item.name;
            itemMetaCache[item.id] = {
                name: item.name,
                type: item.type,
                runtimeMinutes: item.runtimeMinutes
            };
        });
    }

    function itemLabel(id) {
        if (!id) return 'Unknown item';
        return itemTitleCache[id] || id;
    }

    function isRerunSlot(slot) {
        return !!(slot && (slot.isRerunSlot || slot.IsRerunSlot));
    }

    function slotSpan(slot) {
        return Math.max(1, Math.min(8, Number(slot?.spanSlots ?? slot?.SpanSlots ?? 1) || 1));
    }

    function spanFromRuntimeMinutes(runtimeMinutes) {
        const minutes = Number(runtimeMinutes);
        if (!minutes || minutes <= 0) {
            return 1;
        }

        return Math.max(1, Math.min(8, Math.ceil(minutes / 30)));
    }

    function compactLineupSlots(slots) {
        const occupied = new Array(48).fill(false);
        const kept = [];
        [...(slots || [])]
            .sort((a, b) => (a.slotIndex - b.slotIndex))
            .forEach((slot) => {
                const index = slot.slotIndex;
                if (index < 0 || index > 47 || occupied[index]) {
                    return;
                }

                const first = (slot.candidates || slot.Candidates || [])[0];
                const itemId = first?.jellyfinItemId || first?.JellyfinItemId;
                const meta = itemId ? itemMetaCache[itemId] : null;
                const type = String(meta?.type || '').toLowerCase();
                if ((type === 'movie' || type === 'episode') && Number(meta.runtimeMinutes) > 30) {
                    slot.spanSlots = Math.max(slotSpan(slot), spanFromRuntimeMinutes(meta.runtimeMinutes));
                }

                const span = slotSpan(slot);
                slot.spanSlots = span;
                for (let i = index; i < index + span && i < 48; i++) {
                    occupied[i] = true;
                }

                kept.push(slot);
            });
        return kept;
    }

    function applyLineupSlotKinds(slots, kinds) {
        if (!kinds || typeof kinds !== 'object') {
            return;
        }
        (slots || []).forEach((slot) => {
            const kind = kinds[slot.slotIndex] ?? kinds[String(slot.slotIndex)];
            if (kind) {
                slot.slotKind = kind;
                slot.isShortEpisodeBlock = kind === 'short-block';
            }
        });
    }

    function lineupSlotColorClass(slot) {
        if (!slot || lineupIsWeather || isRerunSlot(slot)) {
            return '';
        }
        const kind = String(slot.slotKind || '').toLowerCase().replace(/_/g, '-');
        if (slot.isShortEpisodeBlock || kind === 'short-block' || kind === 'shortblock') {
            return 'type-short-block';
        }
        if (kind === 'movie') {
            return 'type-movie';
        }
        if (kind === 'tvshow' || kind === 'tv-show') {
            return 'type-tvshow';
        }
        return typeClassFromCandidate((slot.candidates || slot.Candidates || [])[0]);
    }

    function typeClassFromCandidate(candidate) {
        if (!candidate) {
            return '';
        }
        const itemId = candidate.jellyfinItemId || candidate.JellyfinItemId;
        const meta = itemId ? itemMetaCache[itemId] : null;
        if (!meta || !meta.type) {
            return '';
        }
        const type = String(meta.type).toLowerCase();
        if (type === 'movie') {
            return 'type-movie';
        }
        if (type === 'musicvideo' || type === 'audio' || type === 'music') {
            return '';
        }
        const runtime = Number(meta.runtimeMinutes);
        if ((type === 'episode' || type === 'series') && runtime > 0 && runtime < 18) {
            return 'type-short-block';
        }
        if (type === 'episode' || type === 'series' || type === 'season') {
            return 'type-tvshow';
        }
        return '';
    }

    function candidateKind(candidate) {
        return resolveEnumValue(SLOT_CANDIDATE_KIND_VALUES, candidate?.kind ?? candidate?.Kind, 0);
    }

    function candidateSummary(candidate) {
        if (!candidate) return 'Empty slot';
        const kind = candidateKind(candidate);
        const itemId = candidate.jellyfinItemId || candidate.JellyfinItemId;
        const collectionName = candidate.collectionName || candidate.CollectionName;
        const filterJson = candidate.filterJson || candidate.FilterJson;
        const finTvListId = candidate.finTvListId || candidate.FinTvListId;

        if (kind === 1 && collectionName) return `Collection: ${collectionName}`;
        if (kind === 2 && filterJson) return 'Filter query';
        if (kind === 3 && finTvListId) return `List: ${listNameCache[finTvListId] || finTvListId}`;
        if (itemId) return itemLabel(itemId);
        return CANDIDATE_KINDS[kind] || 'Candidate';
    }

    function slotIndexFromTime(value) {
        if (!value) return 0;
        const parts = value.split(':');
        const h = parseInt(parts[0], 10) || 0;
        const m = parseInt(parts[1], 10) || 0;
        return Math.min(47, Math.floor((h * 60 + m) / 30));
    }

    function slotTimeInputValue(index) {
        const totalMinutes = index * 30;
        const h = Math.floor(totalMinutes / 60);
        const m = totalMinutes % 60;
        return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}`;
    }

    async function ensureFinTvLists(force) {
        if (!force && finTvLists.length) {
            return finTvLists;
        }

        finTvLists = await api('/lists') || [];
        listNameCache = {};
        finTvLists.forEach((list) => {
            listNameCache[list.id] = list.name;
        });
        return finTvLists;
    }

    async function refreshDashboardStats() {
        try {
            const commercials = await api('/commercials').catch(() => []);
            const enabled = channels.filter((c) => c.enabled).length;
            $('dashboard-stats').innerHTML = [
                `<span class="stat-pill"><strong>${channels.length}</strong> channels</span>`,
                `<span class="stat-pill"><strong>${enabled}</strong> enabled</span>`,
                `<span class="stat-pill"><strong>${(commercials || []).length}</strong> commercials</span>`
            ].join('');
        } catch (e) {
            $('dashboard-stats').innerHTML = '';
        }
    }

    function updateSplitLayout() {
        const layout = q('.split-layout');
        const panel = $('channel-form-panel');
        if (layout && panel) {
            layout.classList.toggle('has-panel', !panel.classList.contains('hidden'));
        }
    }

    function toggleWeatherFields() {
        [['ch-content-type', 'weather-fields'], ['deep-ch-content-type', 'deep-weather-fields']].forEach(([typeId, fieldsId]) => {
            const contentType = $(typeId);
            const weatherFields = $(fieldsId);
            if (!contentType || !weatherFields) {
                return;
            }

            weatherFields.classList.toggle('hidden', contentType.value !== '4');
        });
    }

    function populateLogoSelectors(channel, prefix) {
        prefix = prefix || 'ch';
        const setSelect = $(prefix + '-logo-set');
        const fileSelect = $(prefix + '-logo-file');
        if (!setSelect || !fileSelect) {
            return;
        }

        setSelect.innerHTML = '<option value="">None</option>' + logoSets.map((s) =>
            `<option value="${s.id}">${escapeHtml(s.name)} (${(s.entries || []).length})</option>`).join('');
        setSelect.value = channel?.logoSetId || '';

        const set = logoSets.find((s) => s.id === (channel?.logoSetId || setSelect.value));
        fileSelect.innerHTML = '<option value="">Default</option>' + ((set && set.entries) || []).map((e) =>
            `<option value="${escapeHtml(e.fileName)}">${escapeHtml(e.displayName || e.fileName)}</option>`).join('');
        fileSelect.value = channel?.logoFileName || '';
    }

    async function loadLogoSetsForForm() {
        logoSets = await api('/logos/sets') || [];
    }

    async function loadChannelNowPlaying(channelId) {
        const box = $('channel-now-playing');
        if (!channelId) {
            box.classList.add('hidden');
            return;
        }
        try {
            const response = await api(`/channels/${channelId}/now-playing`).catch(() => null);
            const now = response?.item;
            if (now?.title) {
                box.classList.remove('hidden');
                box.innerHTML = `<strong>Now playing</strong>${escapeHtml(now.title)}`;
            } else {
                box.classList.add('hidden');
            }
        } catch {
            box.classList.add('hidden');
        }
    }

    function channelViewerCount(channelId) {
        return channelOnAir[String(channelId).toLowerCase()] || 0;
    }

    function renderChannelStatusBadges(channel) {
        const viewers = channelViewerCount(channel.id);
        const onAir = viewers > 0;
        const viewerLabel = viewers > 1 ? ` (${viewers})` : '';
        return `<div class="status-badges">
            <span class="badge ${channel.enabled ? 'badge-on' : 'badge-off'}">${channel.enabled ? 'On' : 'Off'}</span>
            <span class="badge ${onAir ? 'badge-air' : 'badge-idle'}">${onAir ? `On Air${viewerLabel}` : 'Off Air'}</span>
        </div>`;
    }

    async function loadChannelOnAirStatus() {
        try {
            const data = await api('/channels/on-air');
            const next = {};
            (data?.channels || []).forEach((entry) => {
                const id = entry.channelId || entry.id;
                const count = Number(entry.viewerCount ?? entry.viewers ?? 0);
                if (id && count > 0) {
                    next[String(id).toLowerCase()] = count;
                }
            });
            channelOnAir = next;
        } catch (err) {
            channelOnAir = {};
            const message = String((err && err.message) || '');
            if (isNetworkError(err) || message === 'Sign in required') {
                stopOnAirPolling();
            }
        }

        renderChannelsList();
    }

    function startOnAirPolling() {
        stopOnAirPolling();
        loadChannelOnAirStatus();
        onAirRefreshTimer = setInterval(loadChannelOnAirStatus, 10000);
    }

    function stopOnAirPolling() {
        if (onAirRefreshTimer) {
            clearInterval(onAirRefreshTimer);
            onAirRefreshTimer = null;
        }
    }

    function filteredChannels() {
        if (!channelFilter) return channels;
        const q = channelFilter.toLowerCase();
        return channels.filter((c) =>
            c.name.toLowerCase().includes(q) ||
            formatChannelNumber(c.number).includes(q) ||
            contentTypeLabel(c.contentType).toLowerCase().includes(q));
    }

    function renderChannelsList() {
        const list = filteredChannels();
        const wrap = $('channels-list');
        if (list.length === 0) {
            wrap.innerHTML = '<div class="empty-state">No channels yet. Click <strong>New Channel</strong> to create one.</div>';
            return;
        }

        wrap.innerHTML = `<table class="data-table">
            <thead><tr><th>#</th><th>Name</th><th>Type</th><th>Status</th><th></th></tr></thead>
            <tbody>${list.map((c) => `<tr data-id="${c.id}" class="${editingChannelId === c.id ? 'selected' : ''}">
                <td><strong>${formatChannelNumber(c.number)}</strong></td>
                <td>${escapeHtml(c.name)}</td>
                <td><span class="badge badge-type">${contentTypeLabel(c.contentType)}</span></td>
                <td>${renderChannelStatusBadges(c)}</td>
                <td class="row-actions">
                    <button type="button" data-quick-edit="${c.id}">Quick Edit</button>
                    <button type="button" data-edit="${c.id}">Edit</button>
                    <button type="button" data-lineup="${c.id}">Lineup</button>
                    <button type="button" class="btn-danger" data-delete="${c.id}">Delete</button>
                </td>
            </tr>`).join('')}</tbody></table>`;

        wrap.querySelectorAll('[data-quick-edit]').forEach((btn) => btn.onclick = (e) => {
            e.stopPropagation();
            editChannel(btn.dataset.quickEdit);
        });
        wrap.querySelectorAll('[data-edit]').forEach((btn) => btn.onclick = (e) => {
            e.stopPropagation();
            openDeepChannelEditor(btn.dataset.edit).catch((err) => reportApiError(err, 'Could not open channel editor.'));
        });
        wrap.querySelectorAll('[data-lineup]').forEach((btn) => btn.onclick = (e) => {
            e.stopPropagation();
            selectedChannelId = btn.dataset.lineup;
            switchTab('lineups');
        });
        wrap.querySelectorAll('[data-delete]').forEach((btn) => btn.onclick = (e) => {
            e.stopPropagation();
            deleteChannel(btn.dataset.delete);
        });
        wrap.querySelectorAll('tbody tr').forEach((row) => row.onclick = () => editChannel(row.dataset.id));
    }

    async function loadChannels() {
        try {
            channels = (await api('/channels') || []).map(normalizeChannel);
            renderChannelsList();

            const select = $('lineup-channel-select');
            select.innerHTML = channels.map((c) =>
                `<option value="${c.id}">${formatChannelNumber(c.number)} - ${escapeHtml(c.name)}</option>`).join('');
            if (!selectedChannelId && channels[0]) selectedChannelId = channels[0].id;
            select.value = selectedChannelId || '';

            populateSpecialChannelSelect();
            populateMapChannelSelect();

            await refreshDashboardStats();
        } catch (err) {
            reportApiError(err, 'Could not load channels.');
        }
    }

    function resetChannelForm() {
        editingChannelId = null;
        const form = $('channel-form');
        if (form) {
            form.reset();
        }

        const audio = $('ch-audio');
        if (audio) {
            audio.value = 'eng';
        }

        const enabled = $('ch-enabled');
        if (enabled) {
            enabled.checked = true;
        }

        const title = $('channel-form-title');
        if (title) {
            title.textContent = 'New Channel';
        }

        const deleteBtn = $('btn-delete-channel');
        if (deleteBtn) {
            deleteBtn.classList.add('hidden');
        }

        const nowPlaying = $('channel-now-playing');
        if (nowPlaying) {
            nowPlaying.classList.add('hidden');
        }

        populateLogoSelectors(null);
        toggleWeatherFields();
    }

    async function openNewChannelForm() {
        if (!syncConfigPage()) {
            return;
        }

        resetChannelForm();
        showChannelForm(true);

        try {
            await loadLogoSetsForForm();
            populateLogoSelectors(null);
        } catch (err) {
            toast(err.message || 'Could not load logo sets.', 'error');
        }
    }

    function showChannelForm(show) {
        const panel = $('channel-form-panel');
        if (!panel) {
            return;
        }

        panel.classList.toggle('hidden', !show);
        updateSplitLayout();
        if (!show) {
            resetChannelForm();
        }
    }

    async function editChannel(id) {
        const c = channels.find((x) => x.id === id);
        if (!c) return;

        try {
            await loadLogoSetsForForm();
        } catch (err) {
            reportApiError(err, 'Could not load logo sets.');
            return;
        }

        editingChannelId = id;
        showChannelForm(true);
        $('channel-form-title').textContent = `Quick Edit ${c.name}`;
        $('btn-delete-channel').classList.remove('hidden');

        $('ch-number').value = c.number;
        $('ch-name').value = c.name;
        setSelectEnum('ch-content-type', CONTENT_TYPE_VALUES, c.contentType, 0);
        setSelectEnum('ch-aspect', ASPECT_RATIO_VALUES, c.aspectRatio, 0);
        setSelectEnum('ch-bug', BUG_PLACEMENT_VALUES, c.bugPlacement, DEFAULT_BUG_PLACEMENT);
        $('ch-audio').value = c.audioLanguage || 'eng';
        if ($('ch-weather-location')) {
            $('ch-weather-location').value = parseWeatherLocationQuery(c.weatherLocationQuery) || '';
        }
        $('ch-enabled').checked = c.enabled;
        populateLogoSelectors(c);
        toggleWeatherFields();
        renderChannelsList();
        loadChannelNowPlaying(c.id);
    }

    async function saveChannel(e) {
        e.preventDefault();
        syncConfigPageFromEvent(e);
        if (!configPage) {
            toast('ChannelFlow-Server is not ready. Reload the page.', 'error');
            return;
        }

        const nameEl = $('ch-name');
        const numberEl = $('ch-number');
        const form = $('channel-form');
        const submitBtn = form ? form.querySelector('button[type="submit"]') : null;
        const originalLabel = submitBtn ? submitBtn.textContent : '';

        if (!nameEl || !numberEl) {
            toast('Channel form is not loaded. Close and reopen the form.', 'error');
            return;
        }

        let number;
        try {
            number = parseChannelNumber(numberEl.value);
        } catch (err) {
            toast(err.message, 'error');
            return;
        }

        let weatherLocationQuery = null;
        try {
            weatherLocationQuery = parseWeatherLocationQuery($('ch-weather-location')?.value);
        } catch (err) {
            toast(err.message, 'error');
            return;
        }

        const payload = buildChannelPayload({
            number,
            name: nameEl.value.trim(),
            contentType: readSelectEnum('ch-content-type', CONTENT_TYPE_VALUES, 0),
            aspectRatio: readSelectEnum('ch-aspect', ASPECT_RATIO_VALUES, 0),
            bugPlacement: readSelectEnum('ch-bug', BUG_PLACEMENT_VALUES, DEFAULT_BUG_PLACEMENT),
            audioLanguage: $('ch-audio')?.value.trim() || 'eng',
            logoSetId: $('ch-logo-set')?.value ? $('ch-logo-set').value : null,
            logoFileName: $('ch-logo-file')?.value || null,
            weatherLocationQuery,
            enabled: !!$('ch-enabled')?.checked
        });

        if (!payload.Name) {
            toast('Channel name is required.', 'error');
            return;
        }

        try {
            if (submitBtn) {
                submitBtn.disabled = true;
                submitBtn.textContent = 'Saving…';
            }

            if (editingChannelId) {
                await api('/channels/' + editingChannelId, { method: 'PUT', body: JSON.stringify(payload) });
                toast('Channel updated.', 'success');
            } else {
                await api('/channels', { method: 'POST', body: JSON.stringify(payload) });
                toast('Channel created.', 'success');
            }
            showChannelForm(false);
            await loadChannels();
        } catch (err) {
            reportApiError(err, 'Could not save channel.');
        } finally {
            if (submitBtn) {
                submitBtn.disabled = false;
                submitBtn.textContent = originalLabel || 'Save';
            }
        }
    }

    function channelEditorPath(id) {
        return '/channels/' + id + '/edit';
    }

    function channelEditorIdFromPath(pathname) {
        const match = normalizePathname(pathname).match(/^\/channels\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\/edit$/i);
        return match ? match[1] : null;
    }

    function parseChannelFilter(filterJson) {
        if (!filterJson) {
            return {};
        }

        try {
            return typeof filterJson === 'string' ? (JSON.parse(filterJson) || {}) : (filterJson || {});
        } catch {
            return {};
        }
    }

    function libraryTagFromFilter(filterJson) {
        const filter = parseChannelFilter(filterJson);
        if (filter.presetId) {
            return String(filter.presetId);
        }

        const tags = Array.isArray(filter.tags) ? filter.tags : [];
        return tags.find((tag) => /^(?:channelflow|fintv)-/i.test(tag)) || '';
    }

    function mergeLibraryTagIntoFilter(existingJson, libraryTag) {
        const filter = parseChannelFilter(existingJson);
        const tag = String(libraryTag || '').trim();
        const otherTags = (Array.isArray(filter.tags) ? filter.tags : [])
            .filter((item) => item && !/^(?:channelflow|fintv)-/i.test(item) && item !== tag);
        if (tag) {
            filter.presetId = tag;
        } else {
            delete filter.presetId;
        }
        if (otherTags.length) {
            filter.tags = otherTags;
        } else {
            delete filter.tags;
        }

        return Object.keys(filter).length ? JSON.stringify(filter) : null;
    }

    function playlistSpotCount(playlist) {
        if (!playlist) {
            return 0;
        }

        const count = playlist.itemCount ?? playlist.itemCount;
        if (count != null) {
            return count;
        }

        const matched = playlist.lastMatchedCount ?? playlist.lastMatchedCount;
        if (matched != null) {
            return matched;
        }

        return (playlist.videoSbids || playlist.videoSbids || []).length;
    }

    function setDeepEditorVisible(show) {
        const editor = $('channel-deep-editor');
        const app = $('channelflow-app');
        if (editor) {
            editor.classList.toggle('hidden', !show);
            editor.hidden = !show;
        }

        if (app) {
            app.classList.toggle('hidden', !!show);
        }

        document.body.classList.toggle('channel-editor-open', !!show);
        if (!show) {
            deepEditingChannelId = null;
            deepChannelPlaylistIds = [];
        }
    }

    function findSearchPlaylist(id) {
        return commercialSearchPlaylists.find((p) => String(p.id) === String(id));
    }

    function fillPlaylistPicker(pickerId, addBtnId, assignedIds) {
        const picker = $(pickerId);
        const addBtn = $(addBtnId);
        if (!picker) {
            return;
        }

        const assigned = new Set((assignedIds || []).map(String));
        const available = commercialSearchPlaylists.filter((p) => !assigned.has(String(p.id)));
        picker.innerHTML = available.length
            ? available.map((p) => `<option value="${escapeHtml(p.id)}">${escapeHtml(p.name)}</option>`).join('')
            : '<option value="">No more playlists</option>';
        picker.disabled = available.length === 0;
        if (addBtn) {
            addBtn.disabled = available.length === 0;
        }
    }

    function playlistChipHtml(id) {
        const playlist = findSearchPlaylist(id);
        const name = playlist ? playlist.name : 'Missing playlist';
        const summary = playlist ? (playlist.filterSummary || playlist.query || '') : '';
        const count = playlistSpotCount(playlist);
        const missing = playlist ? '' : ' is-missing';
        return `<div class="playlist-chip${missing}" data-id="${escapeHtml(id)}">
                <div class="playlist-chip-main">
                    <strong>${escapeHtml(name)}</strong>
                    ${summary ? `<span>${escapeHtml(summary)}</span>` : ''}
                </div>
                <span class="playlist-chip-count">${count} spot${count === 1 ? '' : 's'}</span>
                <button type="button" class="btn-ghost" data-remove-playlist="${escapeHtml(id)}" title="Remove">&times;</button>
            </div>`;
    }

    function renderPlaylistChips(wrapId, assignedIds, pickerId, addBtnId, onRemove) {
        const wrap = $(wrapId);
        if (!wrap) {
            return;
        }

        if (!assignedIds.length) {
            wrap.innerHTML = commercialSearchPlaylists.length
                ? '<div class="playlist-empty">No playlists assigned. Commercial breaks use the default pool until you add one.</div>'
                : '<div class="playlist-empty">No saved playlists yet. Save filters from the CommercialBrainz tab.</div>';
        } else {
            wrap.innerHTML = assignedIds.map(playlistChipHtml).join('');
            wrap.querySelectorAll('[data-remove-playlist]').forEach((btn) => {
                btn.onclick = () => onRemove(btn.dataset.removePlaylist);
            });
        }

        fillPlaylistPicker(pickerId, addBtnId, assignedIds);
    }

    function renderDeepChannelPlaylists() {
        renderPlaylistChips(
            'deep-ch-playlists',
            deepChannelPlaylistIds,
            'deep-ch-playlist-picker',
            'btn-deep-ch-add-playlist',
            removeDeepChannelPlaylist
        );
    }

    function addDeepChannelPlaylist() {
        const picker = $('deep-ch-playlist-picker');
        const id = picker && picker.value;
        if (!id || deepChannelPlaylistIds.some((item) => String(item) === String(id))) {
            return;
        }

        deepChannelPlaylistIds.push(id);
        renderDeepChannelPlaylists();
    }

    function removeDeepChannelPlaylist(id) {
        deepChannelPlaylistIds = deepChannelPlaylistIds.filter((item) => String(item) !== String(id));
        renderDeepChannelPlaylists();
    }

    function commercialsSubtitle() {
        return commercialsPage === 'map'
            ? 'Assign commercial playlists to channels'
            : TAB_SUBTITLES.commercials;
    }

    function applyCommercialsPage() {
        const isMap = commercialsPage === 'map';
        const library = $('commercials-page-library');
        const map = $('commercials-page-map');
        if (library) {
            library.classList.toggle('hidden', isMap);
            library.hidden = isMap;
        }
        if (map) {
            map.classList.toggle('hidden', !isMap);
            map.hidden = !isMap;
        }
        qa('[data-commercials-page]').forEach((btn) => {
            const on = btn.dataset.commercialsPage === commercialsPage;
            btn.classList.toggle('active', on);
            btn.setAttribute('aria-selected', on ? 'true' : 'false');
        });
    }

    function switchCommercialsPage(page) {
        commercialsPage = page === 'map' ? 'map' : 'library';
        applyCommercialsPage();
        window.dispatchEvent(new CustomEvent('channelflow-tabchange', {
            detail: { tab: 'commercials', title: TAB_TITLES.commercials, subtitle: commercialsSubtitle() }
        }));
        if (commercialsPage === 'map') {
            loadChannelPlaylistMap().catch((err) => reportApiError(err, 'Could not load channel mapping.'));
        }
    }

    function openMapChannelsForChannel(channelId) {
        if (channelId) {
            mapChannelId = channelId;
        }
        commercialsPage = 'map';
        closeDeepChannelEditor({ skipHistory: true, stay: true });
        switchTab('commercials');
    }

    function populateMapChannelSelect() {
        const select = $('map-ch-select');
        if (!select) {
            return;
        }

        const previous = mapChannelId || select.value;
        select.innerHTML = channels.length
            ? channels.map((c) =>
                `<option value="${escapeHtml(c.id)}">${escapeHtml(formatChannelNumber(c.number) + ' · ' + c.name)}</option>`).join('')
            : '<option value="">No channels</option>';
        select.disabled = channels.length === 0;

        if (previous && channels.some((c) => String(c.id) === String(previous))) {
            mapChannelId = previous;
            select.value = previous;
        } else if (channels[0]) {
            mapChannelId = channels[0].id;
            select.value = mapChannelId;
        } else {
            mapChannelId = null;
            select.value = '';
        }
    }

    function loadMapChannelAssignment() {
        const select = $('map-ch-select');
        mapChannelId = (select && select.value) || mapChannelId;
        const channel = channels.find((c) => String(c.id) === String(mapChannelId));
        mapChannelPlaylistIds = channel && Array.isArray(channel.commercialSearchPlaylistIds)
            ? channel.commercialSearchPlaylistIds.slice()
            : [];
        renderMapChannelPlaylists();
        renderChannelPlaylistOverview();
    }

    function renderMapChannelPlaylists() {
        renderPlaylistChips(
            'map-ch-playlists',
            mapChannelPlaylistIds,
            'map-ch-playlist-picker',
            'btn-map-ch-add-playlist',
            removeMapChannelPlaylist
        );
    }

    function addMapChannelPlaylist() {
        const picker = $('map-ch-playlist-picker');
        const id = picker && picker.value;
        if (!id || mapChannelPlaylistIds.some((item) => String(item) === String(id))) {
            return;
        }

        mapChannelPlaylistIds.push(id);
        renderMapChannelPlaylists();
    }

    function removeMapChannelPlaylist(id) {
        mapChannelPlaylistIds = mapChannelPlaylistIds.filter((item) => String(item) !== String(id));
        renderMapChannelPlaylists();
    }

    function mappedPlaylistLabel(ids) {
        if (!ids || !ids.length) {
            return 'Default pool';
        }

        return ids.map((id) => {
            const playlist = findSearchPlaylist(id);
            return playlist ? playlist.name : 'Missing playlist';
        }).join(', ');
    }

    function renderChannelPlaylistOverview() {
        const el = $('map-ch-overview');
        if (!el) {
            return;
        }

        if (!channels.length) {
            el.innerHTML = '<div class="empty-state">No channels yet. Create a channel first.</div>';
            return;
        }

        el.innerHTML = `<table class="data-table">
            <thead><tr><th>Channel</th><th>Playlists</th></tr></thead>
            <tbody>${channels.map((c) => {
                const selected = String(c.id) === String(mapChannelId) ? ' class="selected"' : '';
                const names = mappedPlaylistLabel(c.commercialSearchPlaylistIds);
                return `<tr data-channel-id="${escapeHtml(c.id)}"${selected}>
                    <td><strong>${escapeHtml(formatChannelNumber(c.number))}</strong> ${escapeHtml(c.name)}</td>
                    <td>${escapeHtml(names)}</td>
                </tr>`;
            }).join('')}</tbody></table>`;

        el.querySelectorAll('tbody tr').forEach((row) => {
            row.onclick = () => {
                mapChannelId = row.dataset.channelId;
                populateMapChannelSelect();
                loadMapChannelAssignment();
            };
        });
    }

    function refreshMappedPlaylistViews() {
        if (document.body.classList.contains('channel-editor-open')) {
            renderDeepChannelPlaylists();
        }
        if (commercialsPage === 'map') {
            renderMapChannelPlaylists();
            renderChannelPlaylistOverview();
        }
    }

    function channelPlaylistPayload(channel, playlistIds) {
        return buildChannelPayload({
            channel,
            number: channel.number,
            name: channel.name,
            contentType: channel.contentType,
            aspectRatio: channel.aspectRatio,
            bugPlacement: channel.bugPlacement,
            audioLanguage: channel.audioLanguage || 'eng',
            logoSetId: channel.logoSetId || null,
            logoFileName: channel.logoFileName || null,
            weatherLocationQuery: channel.weatherLocationQuery || null,
            enabled: channel.enabled !== false,
            deep: {
                CommercialSearchPlaylistIds: playlistIds.slice()
            }
        });
    }

    async function loadChannelPlaylistMap() {
        applyCommercialsPage();
        if (!channels.length) {
            await loadChannels();
        } else {
            populateMapChannelSelect();
        }

        if (!commercialSearchPlaylists.length) {
            try {
                commercialSearchPlaylists = await api('/commercials/search-playlists') || [];
            } catch (err) {
                commercialSearchPlaylists = commercialSearchPlaylists || [];
            }
        }

        loadMapChannelAssignment();
    }

    async function saveChannelPlaylistMap() {
        const channel = channels.find((c) => String(c.id) === String(mapChannelId));
        if (!channel) {
            toast('Pick a channel first.', 'error');
            return;
        }

        const saveBtn = $('btn-map-ch-save');
        const originalLabel = saveBtn ? saveBtn.textContent : '';
        try {
            if (saveBtn) {
                saveBtn.disabled = true;
                saveBtn.textContent = 'Saving…';
            }

            await api('/channels/' + channel.id, {
                method: 'PUT',
                body: JSON.stringify(channelPlaylistPayload(channel, mapChannelPlaylistIds))
            });
            toast('Channel mapping saved.', 'success');
            await loadChannels();
            populateMapChannelSelect();
            loadMapChannelAssignment();
        } catch (err) {
            reportApiError(err, 'Could not save channel mapping.');
        } finally {
            if (saveBtn) {
                saveBtn.disabled = false;
                saveBtn.textContent = originalLabel || 'Save';
            }
        }
    }

    function fillDeepChannelForm(channel) {
        deepEditingChannelId = channel.id;
        deepChannelPlaylistIds = (channel.commercialSearchPlaylistIds || []).slice();
        $('deep-ch-title').textContent = channel.name;
        $('deep-ch-number').value = channel.number;
        $('deep-ch-name').value = channel.name;
        setSelectEnum('deep-ch-content-type', CONTENT_TYPE_VALUES, channel.contentType, 0);
        setSelectEnum('deep-ch-aspect', ASPECT_RATIO_VALUES, channel.aspectRatio, 0);
        setSelectEnum('deep-ch-bug', BUG_PLACEMENT_VALUES, channel.bugPlacement, DEFAULT_BUG_PLACEMENT);
        $('deep-ch-audio').value = channel.audioLanguage || 'eng';
        if ($('deep-ch-weather-location')) {
            $('deep-ch-weather-location').value = parseWeatherLocationQuery(channel.weatherLocationQuery) || '';
        }
        $('deep-ch-enabled').checked = channel.enabled !== false;
        $('deep-ch-library-tag').value = libraryTagFromFilter(channel.filterJson);
        setSelectEnum('deep-ch-catalog-mode', { 0: 0, 1: 1, 2: 2, 3: 3 }, channel.catalogMode, 2);
        $('deep-ch-ai-prompt').value = channel.aiFineTunePrompt || '';
        populateLogoSelectors(channel, 'deep-ch');
        toggleWeatherFields();
        renderDeepChannelPlaylists();
    }

    async function openDeepChannelEditor(id, options) {
        options = options || {};
        const channel = channels.find((x) => String(x.id) === String(id));
        if (!channel) {
            toast('Channel not found.', 'error');
            return;
        }

        try {
            await loadLogoSetsForForm();
        } catch (err) {
            reportApiError(err, 'Could not load logo sets.');
            return;
        }

        try {
            commercialSearchPlaylists = await api('/commercials/search-playlists') || [];
        } catch (err) {
            commercialSearchPlaylists = commercialSearchPlaylists || [];
        }

        fillDeepChannelForm(channel);
        setDeepEditorVisible(true);
        document.title = 'Edit ' + channel.name + ' · ChannelFlow-Server';
        window.dispatchEvent(new CustomEvent('channelflow-tabchange', {
            detail: {
                tab: 'channels',
                title: 'Edit ' + channel.name,
                subtitle: formatChannelNumber(channel.number) + ' · Deep channel options'
            }
        }));

        const path = channelEditorPath(channel.id);
        if (!options.fromRoute && normalizePathname(location.pathname) !== path) {
            history.pushState({ tab: 'channel-editor', id: channel.id }, '', withAppBase(path));
        }
    }

    function closeDeepChannelEditor(options) {
        options = options || {};
        const wasOpen = document.body.classList.contains('channel-editor-open');
        setDeepEditorVisible(false);
        if (!wasOpen) {
            return;
        }

        if (!options.skipHistory && channelEditorIdFromPath(location.pathname)) {
            history.pushState({ tab: 'channels' }, '', withAppBase('/channels'));
        }
    }

    async function saveDeepChannel() {
        const channel = channels.find((x) => String(x.id) === String(deepEditingChannelId));
        if (!channel) {
            toast('Channel not found.', 'error');
            return;
        }

        let number;
        try {
            number = parseChannelNumber($('deep-ch-number').value);
        } catch (err) {
            toast(err.message, 'error');
            return;
        }

        let weatherLocationQuery = null;
        try {
            weatherLocationQuery = parseWeatherLocationQuery($('deep-ch-weather-location')?.value);
        } catch (err) {
            toast(err.message, 'error');
            return;
        }

        const name = ($('deep-ch-name')?.value || '').trim();
        if (!name) {
            toast('Channel name is required.', 'error');
            return;
        }

        const payload = buildChannelPayload({
            channel,
            number,
            name,
            contentType: readSelectEnum('deep-ch-content-type', CONTENT_TYPE_VALUES, 0),
            aspectRatio: readSelectEnum('deep-ch-aspect', ASPECT_RATIO_VALUES, 0),
            bugPlacement: readSelectEnum('deep-ch-bug', BUG_PLACEMENT_VALUES, DEFAULT_BUG_PLACEMENT),
            audioLanguage: $('deep-ch-audio')?.value.trim() || 'eng',
            logoSetId: $('deep-ch-logo-set')?.value ? $('deep-ch-logo-set').value : null,
            logoFileName: $('deep-ch-logo-file')?.value || null,
            weatherLocationQuery,
            enabled: !!$('deep-ch-enabled')?.checked,
            deep: {
                FilterJson: mergeLibraryTagIntoFilter(channel.filterJson, $('deep-ch-library-tag')?.value),
                CatalogMode: readSelectEnum('deep-ch-catalog-mode', { 0: 0, 1: 1, 2: 2, 3: 3 }, 2),
                AiFineTunePrompt: ($('deep-ch-ai-prompt')?.value || '').trim() || null,
                CommercialSearchPlaylistIds: deepChannelPlaylistIds.slice()
            }
        });

        const saveBtn = $('btn-deep-ch-save');
        const originalLabel = saveBtn ? saveBtn.textContent : '';
        try {
            if (saveBtn) {
                saveBtn.disabled = true;
                saveBtn.textContent = 'Saving…';
            }

            await api('/channels/' + channel.id, { method: 'PUT', body: JSON.stringify(payload) });
            toast('Channel updated.', 'success');
            await loadChannels();
            const updated = channels.find((x) => String(x.id) === String(channel.id));
            if (updated) {
                fillDeepChannelForm(updated);
            }
        } catch (err) {
            reportApiError(err, 'Could not save channel.');
        } finally {
            if (saveBtn) {
                saveBtn.disabled = false;
                saveBtn.textContent = originalLabel || 'Save';
            }
        }
    }

    async function deleteChannel(channelId) {
        channelId = channelId || editingChannelId;
        if (!channelId) {
            return;
        }

        const c = channels.find((x) => x.id === channelId);
        if (!c) {
            return;
        }

        if (!confirm(`Delete channel ${formatChannelNumber(c.number)} - ${c.name}?`)) {
            return;
        }

        try {
            await api('/channels/' + channelId, { method: 'DELETE' });
            toast('Channel deleted.', 'success');
            if (editingChannelId === channelId) {
                showChannelForm(false);
            }

            if (selectedChannelId === channelId) {
                selectedChannelId = null;
            }

            await loadChannels();
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function collectItemIdsFromSlots(slots) {
        const ids = [];
        (slots || []).forEach((slot) => (slot.candidates || []).forEach((c) => {
            if (c.jellyfinItemId) ids.push(c.jellyfinItemId);
        }));
        await lookupItemTitles(ids);
    }

    async function loadLineups() {
        selectedChannelId = $('lineup-channel-select').value;
        if (!selectedChannelId) return;

        try {
            selectedLineupOverrideId = null;
            const dateVal = $('lineup-preview-date')?.value || todayIsoDate();
            const data = await api('/lineups/' + selectedChannelId + '?date=' + encodeURIComponent(dateVal));
            lineupIsWeather = !!(data.isWeather || getLineupChannel()?.contentType === CONTENT_TYPE_VALUES.Weather);
            lineupSlots = (data.lineup && data.lineup.slots) || [];
            lineupOverrides = data.overrides || [];
            if (lineupIsWeather) {
                lineupSlots = [{ slotIndex: 0, spanSlots: 48, candidates: [] }];
            } else if (lineupSlots.length === 0) {
                lineupSlots = Array.from({ length: 48 }, (_, i) => ({ slotIndex: i, candidates: [] }));
            } else {
                lineupSlots = compactLineupSlots(lineupSlots);
            }
            applyLineupSlotKinds(lineupSlots, data.slotKinds);

            updateLineupToolbarState();

            if (lineupIsWeather) {
                renderWeatherLineupGrid();
                renderOverrideList();
                $('lineup-preview-banner').classList.add('hidden');
                await previewLineup(true);
                return;
            }

            await collectItemIdsFromSlots(lineupSlots);
            lineupSlots = compactLineupSlots(lineupSlots);
            renderLineupGrid();
            renderOverrideList();
            $('lineup-preview-banner').classList.add('hidden');
            await loadLineupPlayoutStatus();
        } catch (err) {
            reportApiError(err, 'Could not load lineup.');
        }
    }

    async function loadLineupPlayoutStatus() {
        const banner = $('lineup-playout-banner');
        if (!banner) {
            return;
        }

        if (!selectedChannelId || lineupIsWeather) {
            banner.classList.add('hidden');
            return;
        }

        try {
            const h = await api('/lineups/' + selectedChannelId + '/playout-horizon');
            const daysBuilt = Number(h.daysBuilt || 0);
            const targetDays = Number(h.playoutDaysToBuild || 14);
            const itemCount = Number(h.playoutItemCount || 0);
            const hasCoverageNow = !!h.hasCoverageNow;

            if (!hasCoverageNow) {
                banner.classList.remove('hidden');
                if (itemCount > 0 && h.earliestStartUtc) {
                    const nextStart = new Date(h.earliestStartUtc).toLocaleString();
                    banner.textContent = `Nothing on air right now. Next programme starts ${nextStart}. Live TV will show Off Air until then.`;
                } else {
                    banner.textContent = 'Live TV guide has no playout for this channel yet. Fill lineup slots (AI Generate or manual), then click Rebuild Playout.';
                }
                return;
            }

            if (!h.latestScheduledFinishUtc || daysBuilt < 0.5) {
                banner.classList.remove('hidden');
                banner.textContent = 'Guide playout is ending soon. Click Rebuild Playout to refresh the schedule.';
                return;
            }

            if (daysBuilt < 1) {
                banner.classList.remove('hidden');
                banner.textContent = `Guide playout ends in about ${Math.max(1, Math.round(daysBuilt * 24))} hours. Rebuild Playout to refresh the full ${targetDays}-day guide.`;
                return;
            }

            banner.classList.add('hidden');
        } catch (_) {
            banner.classList.add('hidden');
        }
    }

    function getLineupChannel() {
        return channels.find((c) => c.id === selectedChannelId);
    }

    function updateLineupToolbarState() {
        const hint = $('lineup-hint');
        const saveBtn = $('btn-save-lineup');
        const addOverrideBtn = $('btn-add-override');
        const overridesSection = $('lineup-overrides-section');
        const weatherBanner = $('lineup-weather-banner');
        const channel = getLineupChannel();

        if (lineupIsWeather) {
            const location = channel?.weatherLocationQuery;
            const coords = location && String(location).trim()
                ? String(location).trim()
                : 'not set';

            if (hint) {
                hint.textContent = 'Weather channels use 24 one-hour Local Weather blocks that play back-to-back all day.';
            }

            if (weatherBanner) {
                weatherBanner.classList.remove('hidden');
                weatherBanner.textContent = `Weather channel · Live WeatherStar capture · Location: ${coords}. Edit location on the Channels tab. Configure display settings on the Weather tab.`;
            }

            saveBtn?.classList.add('hidden');
            addOverrideBtn?.classList.add('hidden');
            overridesSection?.classList.add('hidden');
            return;
        }

        if (hint) {
            const selected = selectedWeekLineup();
            if (selected) {
                hint.textContent = 'Showing ' + weekLineupLabel(selected) + '. Click a 30-minute slot to edit candidates. Save Lineup writes this day’s template.';
            } else {
                hint.textContent = 'Click a 30-minute slot to edit candidates. Check Rerun slot to fill it from yesterday’s primetime (yellow). Click a Weeks lineups row to load that day’s template.';
            }
        }

        weatherBanner?.classList.add('hidden');
        saveBtn?.classList.remove('hidden');
        addOverrideBtn?.classList.remove('hidden');
        overridesSection?.classList.remove('hidden');
    }

    function renderWeatherLineupGrid() {
        const grid = $('lineup-grid');
        grid.innerHTML = Array.from({ length: 24 }, (_, hour) => {
            const start = slotTime(hour * 2);
            const end = hour < 23 ? slotTime((hour + 1) * 2) : '12:00 AM';
            return `<div class="slot-card has-items weather-slot" data-hour="${hour}" style="--slot-span:2;grid-column:span 2">
                <div class="time">${start} – ${end}</div>
                <div class="summary">Local Weather</div>
                <div class="count">Live WeatherStar · 1 hour</div>
            </div>`;
        }).join('');

        grid.querySelectorAll('.weather-slot').forEach((card) => {
            card.onclick = () =>
                toast('Weather channels use 24 hourly live blocks. Edit coordinates on the Channels tab.', 'info');
        });
    }

    function renderLineupGrid() {
        const grid = $('lineup-grid');
        const occupied = new Array(48).fill(false);
        const byIndex = {};
        compactLineupSlots(lineupSlots).forEach((s) => {
            byIndex[s.slotIndex] = s;
        });

        let html = '';
        for (let i = 0; i < 48; i++) {
            if (occupied[i]) {
                continue;
            }

            const s = byIndex[i] || { slotIndex: i, spanSlots: 1, candidates: [] };
            const rerun = isRerunSlot(s);
            const count = (s.candidates || []).length;
            const first = rerun
                ? 'Rerun · yesterday primetime'
                : (count ? candidateSummary(s.candidates[0]) : 'Empty slot');
            const span = slotSpan(s);
            for (let j = i; j < i + span && j < 48; j++) {
                occupied[j] = true;
            }

            const spanLabel = span > 1 ? ` · ${span * 30}m` : '';
            const typeClass = lineupSlotColorClass(s);
            const classes = ['slot-card', rerun ? 'rerun-slot' : (count ? 'has-items' : 'empty'), typeClass, span > 1 ? 'span-block' : '']
                .filter(Boolean)
                .join(' ');
            html += `<div class="${classes}" data-slot="${s.slotIndex}" style="${span > 1 ? '--slot-span:' + span + ';grid-column:span ' + span : ''}">
                <div class="time">${slotTime(s.slotIndex)}${spanLabel}</div>
                <div class="summary">${escapeHtml(first)}</div>
                <div class="count">${rerun ? 'Rerun slot' : `${count} candidate${count === 1 ? '' : 's'}`}</div>
            </div>`;
        }

        grid.innerHTML = html;
        grid.querySelectorAll('.slot-card').forEach((card) => {
            card.onclick = () => openSlotEditor(parseInt(card.dataset.slot, 10));
        });
    }

    function weekLineupLabel(override) {
        if (!override) {
            return 'week lineup';
        }
        if (override.name) {
            return override.name;
        }
        if (override.kind === 1 && override.specificDate) {
            return override.specificDate;
        }
        if (override.dayOfWeek !== undefined && override.dayOfWeek !== null) {
            return DAYS[override.dayOfWeek] || 'week lineup';
        }
        return 'week lineup';
    }

    function selectedWeekLineup() {
        if (!selectedLineupOverrideId) {
            return null;
        }
        return lineupOverrides.find((o) => o.id === selectedLineupOverrideId) || null;
    }

    function cloneLineupSlots(slots) {
        return (slots || []).map((s) => ({
            ...s,
            slotIndex: s.slotIndex ?? s.SlotIndex,
            spanSlots: s.spanSlots ?? s.SpanSlots ?? 1,
            isRerunSlot: !!(s.isRerunSlot || s.IsRerunSlot),
            candidates: (s.candidates || s.Candidates || []).map((c) => ({ ...c }))
        }));
    }

    function applyLoadedLineupSlots(slots, kinds) {
        lineupSlots = compactLineupSlots(cloneLineupSlots(slots));
        if (lineupSlots.length === 0) {
            lineupSlots = Array.from({ length: 48 }, (_, i) => ({ slotIndex: i, candidates: [] }));
        }
        applyLineupSlotKinds(lineupSlots, kinds);
    }

    function isoDateValue(value) {
        if (!value) {
            return '';
        }
        if (typeof value === 'string') {
            return value.slice(0, 10);
        }
        return String(value);
    }

    async function selectWeekLineup(overrideId) {
        if (!overrideId || lineupIsWeather) {
            return;
        }

        const fallback = lineupOverrides.find((o) => o.id === overrideId);
        try {
            const dateVal = $('lineup-preview-date')?.value || todayIsoDate();
            const data = await api('/lineups/overrides/' + overrideId + '?date=' + encodeURIComponent(dateVal));
            selectedLineupOverrideId = data.id || overrideId;
            applyLoadedLineupSlots(data.slots, data.slotKinds);
            const aligned = isoDateValue(data.date);
            const previewDate = $('lineup-preview-date');
            if (aligned && previewDate) {
                previewDate.value = aligned;
            }
        } catch (err) {
            if (!fallback) {
                reportApiError(err, 'Could not load week lineup.');
                return;
            }
            selectedLineupOverrideId = fallback.id;
            applyLoadedLineupSlots(fallback.slots || fallback.Slots, null);
        }

        await collectItemIdsFromSlots(lineupSlots);
        lineupSlots = compactLineupSlots(lineupSlots);
        updateLineupToolbarState();
        renderLineupGrid();
        renderOverrideList();
        $('lineup-preview-banner')?.classList.add('hidden');
        await loadLineupPlayoutStatus();
    }

    function renderOverrideList() {
        const el = $('override-list');
        if (!lineupOverrides.length) {
            el.innerHTML = '<div class="empty-state">No week lineups configured.</div>';
            return;
        }

        el.innerHTML = lineupOverrides.map((o) => {
            const when = o.kind === 1 && o.specificDate
                ? o.specificDate
                : (o.dayOfWeek !== undefined && o.dayOfWeek !== null ? DAYS[o.dayOfWeek] : 'Schedule');
            const selected = o.id === selectedLineupOverrideId ? ' selected' : '';
            return `<div class="override-card${selected}" data-override-id="${o.id}" role="button" tabindex="0" aria-pressed="${o.id === selectedLineupOverrideId ? 'true' : 'false'}">
                <div>
                    <strong>${escapeHtml(o.name)}</strong>
                    <div class="meta">${when} · ${(o.slots || []).filter((s) => (s.candidates || []).length).length} filled slots</div>
                </div>
                <div class="row-actions">
                    <button type="button" data-delete-override="${o.id}">Delete</button>
                </div>
            </div>`;
        }).join('');

        el.querySelectorAll('[data-delete-override]').forEach((btn) => {
            btn.onclick = (ev) => {
                ev.preventDefault();
                ev.stopPropagation();
                deleteOverride(btn.dataset.deleteOverride);
            };
        });
        el.querySelectorAll('.override-card[data-override-id]').forEach((card) => {
            card.onclick = (ev) => {
                if (ev.target.closest('[data-delete-override]')) {
                    return;
                }
                selectWeekLineup(card.dataset.overrideId);
            };
            card.onkeydown = (ev) => {
                if (ev.key !== 'Enter' && ev.key !== ' ') {
                    return;
                }
                ev.preventDefault();
                selectWeekLineup(card.dataset.overrideId);
            };
        });
    }

    function openSlotEditor(index) {
        if (lineupIsWeather) {
            toast('Weather channels use a live 24/7 feed and do not use lineup candidates.', 'info');
            return;
        }

        const slot = lineupSlots.find((s) => s.slotIndex === index) || { slotIndex: index, candidates: [] };
        slot.candidates = slot.candidates || [];
        const channel = channels.find((c) => c.id === selectedChannelId);

        const body = `
            <p class="hint">Editing ${slotTime(index)} · add multiple weighted candidates for smart rotation.</p>
            <label class="field checkbox-field">
                <input id="slot-rerun" type="checkbox" ${isRerunSlot(slot) ? 'checked' : ''}>
                <span class="channelflow-check-box" aria-hidden="true"></span>
                <span>Rerun slot</span>
            </label>
            <p class="hint">Yellow on the grid. Playout copies a primetime show from the previous day into this time. Candidates below are used only if no rerun is available.</p>
            <label class="field"><span>Block length (30-min slots)</span>
                <input id="slot-span-slots" type="number" min="1" max="8" class="emby-input" value="${Math.max(1, slot.spanSlots || 1)}"></label>
            <div id="slot-candidates" class="candidate-list">${renderCandidateRows(slot.candidates)}</div>
            <div class="field">
                <span>Add candidate</span>
                <select id="slot-add-kind" class="emby-select">
                    <option value="0">Jellyfin item</option>
                    <option value="1">Collection name</option>
                    <option value="2">Filter JSON</option>
                    <option value="3">ChannelFlow list</option>
                </select>
            </div>
            <div id="slot-add-panel"></div>`;

        openModal(`Slot ${slotTime(index)}`, body, `
            <button type="button" class="emby-button" id="slot-cancel">Cancel</button>
            <button type="button" class="raised button-submit emby-button" id="slot-save">Save Slot</button>`);

        const panel = document.getElementById('slot-add-panel');
        function renderAddPanel() {
            const kind = parseInt(document.getElementById('slot-add-kind').value, 10);
            if (kind === 0) {
                panel.innerHTML = `
                    <label class="field"><span>Search library</span>
                    <input id="slot-search" type="search" class="emby-input" placeholder="Type at least 2 characters…"></label>
                    <div id="slot-search-results" class="search-results"></div>`;
                let timer;
                document.getElementById('slot-search').oninput = (ev) => {
                    clearTimeout(timer);
                    timer = setTimeout(() => searchLibrary(ev.target.value, channel), 250);
                };
            } else if (kind === 1) {
                panel.innerHTML = `<label class="field"><span>Collection name</span>
                    <input id="slot-collection" class="emby-input"><button type="button" class="emby-button" id="slot-add-collection" style="margin-top:.5rem">Add collection</button></label>`;
                document.getElementById('slot-add-collection').onclick = () => {
                    const name = document.getElementById('slot-collection').value.trim();
                    if (!name) return;
                    slot.candidates.push({ kind: 1, collectionName: name, weight: 1, sortOrder: slot.candidates.length });
                    refreshCandidateList(slot);
                };
            } else if (kind === 2) {
                panel.innerHTML = `<label class="field"><span>Filter JSON</span>
                    <textarea id="slot-filter" class="emby-input" rows="3" placeholder='{"genre":"Comedy"}'></textarea></label>
                    <button type="button" class="emby-button" id="slot-add-filter">Add filter</button>`;
                document.getElementById('slot-add-filter').onclick = () => {
                    const json = document.getElementById('slot-filter').value.trim();
                    if (!json) return;
                    slot.candidates.push({ kind: 2, filterJson: json, weight: 1, sortOrder: slot.candidates.length });
                    refreshCandidateList(slot);
                };
            } else if (kind === 3) {
                ensureFinTvLists().then((lists) => {
                    panel.innerHTML = `<label class="field"><span>ChannelFlow list</span>
                        <select id="slot-list-id" class="emby-select">
                            ${lists.map((l) => `<option value="${l.id}">${escapeHtml(l.name)}</option>`).join('')}
                        </select></label>
                        <button type="button" class="emby-button" id="slot-add-list" style="margin-top:.5rem">Add list</button>`;
                    document.getElementById('slot-add-list').onclick = () => {
                        const listId = document.getElementById('slot-list-id').value;
                        if (!listId) return;
                        slot.candidates.push({ kind: 3, finTvListId: listId, weight: 1, sortOrder: slot.candidates.length });
                        refreshCandidateList(slot);
                    };
                });
            }
        }

        document.getElementById('slot-add-kind').onchange = renderAddPanel;
        renderAddPanel();
        bindCandidateRowActions(slot);

        async function searchLibrary(q, ch) {
            const resultsEl = document.getElementById('slot-search-results');
            if (!q || q.trim().length < 2) {
                resultsEl.innerHTML = '';
                return;
            }
            const params = new URLSearchParams({ q: q.trim(), limit: '20' });
            if (ch) params.set('contentType', ch.contentType);
            const results = await api('/catalog/search?' + params.toString());
            resultsEl.innerHTML = (results || []).map((item) =>
                `<div class="search-result" data-id="${item.id}" data-type="${escapeHtml(item.type || '')}" data-runtime="${item.runtimeMinutes || ''}">
                    <strong>${escapeHtml(item.name)}</strong>
                    <div class="sub">${escapeHtml(item.type)}${item.runtimeMinutes ? ' · ' + item.runtimeMinutes + 'm' : ''}</div>
                </div>`).join('') || '<div class="search-result">No matches</div>';

            resultsEl.querySelectorAll('.search-result[data-id]').forEach((row) => {
                row.onclick = () => {
                    itemTitleCache[row.dataset.id] = row.querySelector('strong').textContent;
                    itemMetaCache[row.dataset.id] = {
                        name: itemTitleCache[row.dataset.id],
                        type: row.dataset.type,
                        runtimeMinutes: row.dataset.runtime ? parseInt(row.dataset.runtime, 10) : null
                    };
                    slot.candidates.push({
                        kind: 0,
                        jellyfinItemId: row.dataset.id,
                        weight: 1,
                        sortOrder: slot.candidates.length
                    });
                    const type = String(row.dataset.type || '').toLowerCase();
                    const runtime = row.dataset.runtime ? parseInt(row.dataset.runtime, 10) : 0;
                    if (type === 'movie' && runtime > 30) {
                        slot.spanSlots = Math.max(slotSpan(slot), spanFromRuntimeMinutes(runtime));
                        const spanInput = document.getElementById('slot-span-slots');
                        if (spanInput) {
                            spanInput.value = String(slot.spanSlots);
                        }
                    }
                    refreshCandidateList(slot);
                    toast('Item added to slot.', 'success');
                };
            });
        }

        document.getElementById('slot-cancel').onclick = closeModal;
        document.getElementById('slot-save').onclick = () => {
            slot.spanSlots = Math.max(1, Math.min(8, parseInt(document.getElementById('slot-span-slots').value, 10) || 1));
            slot.isRerunSlot = !!document.getElementById('slot-rerun')?.checked;
            delete slot.slotKind;
            delete slot.isShortEpisodeBlock;
            lineupSlots.forEach((s) => {
                if (s.slotIndex < index && s.slotIndex + slotSpan(s) > index) {
                    s.spanSlots = Math.max(1, index - s.slotIndex);
                }
            });
            lineupSlots = lineupSlots.filter((s) =>
                s.slotIndex === index
                || s.slotIndex < index
                || s.slotIndex >= index + slot.spanSlots);
            const idx = lineupSlots.findIndex((s) => s.slotIndex === index);
            if (idx >= 0) lineupSlots[idx] = slot;
            else lineupSlots.push(slot);
            lineupSlots = compactLineupSlots(lineupSlots);
            closeModal();
            renderLineupGrid();
            toast('Slot updated. Click Save Lineup to persist.', 'success');
        };
    }

    function renderCandidateRows(candidates) {
        if (!candidates.length) return '<div class="hint">No candidates yet.</div>';
        return candidates.map((c, i) => `<div class="candidate-row">
            <div><div class="title">${escapeHtml(candidateSummary(c))}</div>
            <div class="sub">${CANDIDATE_KINDS[candidateKind(c)] || 'Item'} · weight ${c.weight || 1}</div></div>
            <input type="number" min="1" value="${c.weight || 1}" data-weight="${i}" style="width:70px">
            <button type="button" data-remove-candidate="${i}">Remove</button>
        </div>`).join('');
    }

    function refreshCandidateList(currentSlot, containerId = 'slot-candidates') {
        const container = document.getElementById(containerId);
        if (!container) return;
        container.innerHTML = renderCandidateRows(currentSlot.candidates);
        bindCandidateRowActions(currentSlot, containerId);
    }

    function bindCandidateRowActions(currentSlot, containerId = 'slot-candidates') {
        const container = document.getElementById(containerId);
        if (!container) return;

        container.querySelectorAll('[data-remove-candidate]').forEach((btn) => {
            btn.onclick = () => {
                currentSlot.candidates.splice(parseInt(btn.dataset.removeCandidate, 10), 1);
                currentSlot.candidates.forEach((c, i) => { c.sortOrder = i; });
                refreshCandidateList(currentSlot, containerId);
            };
        });
        container.querySelectorAll('[data-weight]').forEach((input) => {
            input.onchange = () => {
                const idx = parseInt(input.dataset.weight, 10);
                currentSlot.candidates[idx].weight = Math.max(1, parseInt(input.value, 10) || 1);
            };
        });
    }

    async function saveLineup() {
        try {
            const selected = selectedWeekLineup();
            if (selected) {
                await api('/lineups/overrides/' + selected.id, {
                    method: 'PUT',
                    body: JSON.stringify({
                        name: selected.name,
                        kind: selected.kind,
                        dayOfWeek: selected.dayOfWeek,
                        specificDate: selected.specificDate,
                        slots: compactLineupSlots(lineupSlots)
                    })
                });
                selected.slots = cloneLineupSlots(compactLineupSlots(lineupSlots));
                renderOverrideList();
                toast('Week lineup saved. Playout rebuild started in background.', 'success');
            } else {
                await api('/lineups/' + selectedChannelId, { method: 'PUT', body: JSON.stringify(compactLineupSlots(lineupSlots)) });
                toast('Lineup saved. Playout rebuild started in background.', 'success');
            }
            await loadLineupPlayoutStatus();
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function rebuildLineup() {
        const btn = $('btn-rebuild-lineup');
        try {
            if (btn) {
                btn.disabled = true;
            }

            await api('/lineups/' + selectedChannelId + '/rebuild', { method: 'POST' });
            toast('Playout rebuild started in background. Guide status will refresh automatically.', 'success');

            await waitForPlayoutRebuild(selectedChannelId);
        } catch (err) {
            toast(err.message, 'error');
        } finally {
            if (btn) {
                btn.disabled = false;
            }
        }
    }

    async function previewLineup(silent) {
        const dateVal = $('lineup-preview-date').value || todayIsoDate();
        try {
            const data = await api('/lineups/' + selectedChannelId + '/preview', {
                method: 'POST',
                body: JSON.stringify({ date: dateVal })
            });

            if (data.isWeather) {
                $('lineup-preview-banner').classList.remove('hidden');
                $('lineup-preview-banner').textContent = `Preview for ${data.date}: 24/24 hourly blocks — ${data.title || 'Local Weather'} (live).`;
                return;
            }

            (data.slots || []).forEach((s) => {
                const local = lineupSlots.find((x) => x.slotIndex === s.slotIndex);
                if (!local) {
                    return;
                }
                if (s.slotKind) {
                    local.slotKind = s.slotKind;
                    local.isShortEpisodeBlock = !!(s.isShortEpisodeBlock || s.slotKind === 'short-block');
                } else {
                    delete local.slotKind;
                    delete local.isShortEpisodeBlock;
                }
            });
            renderLineupGrid();

            const filled = (data.slots || []).reduce((sum, s) => {
                const count = Number(s.candidateCount || 0);
                if (!count && !isRerunSlot(s)) {
                    return sum;
                }

                return sum + slotSpan(s);
            }, 0);
            $('lineup-preview-banner').classList.remove('hidden');
            $('lineup-preview-banner').textContent = `Preview for ${data.date}: ${filled}/48 slots have candidates.`;
        } catch (err) {
            if (!silent) {
                toast(err.message, 'error');
            }
        }
    }

    function openOverrideForm() {
        const body = `
            <label class="field"><span>Name</span><input id="ov-name" class="emby-input" placeholder="Friday Movie Night"></label>
            <label class="field"><span>Schedule type</span>
                <select id="ov-kind" class="emby-select">
                    <option value="0">Day of week</option>
                    <option value="1">Specific date</option>
                </select>
            </label>
            <label class="field" id="ov-day-wrap"><span>Day</span>
                <select id="ov-day" class="emby-select">${DAYS.map((d, i) => `<option value="${i}">${d}</option>`).join('')}</select>
            </label>
            <label class="field hidden" id="ov-date-wrap"><span>Date</span><input id="ov-date" type="date" class="emby-input"></label>
            <p class="hint">Override starts with empty slots. Edit them on the lineup grid after saving (future enhancement: dedicated override editor).</p>`;

        openModal('Add Override Lineup', body, `
            <button type="button" class="emby-button" id="ov-cancel">Cancel</button>
            <button type="button" class="raised button-submit emby-button" id="ov-save">Create Override</button>`);

        const kindEl = document.getElementById('ov-kind');
        kindEl.onchange = () => {
            const specific = kindEl.value === '1';
            document.getElementById('ov-day-wrap').classList.toggle('hidden', specific);
            document.getElementById('ov-date-wrap').classList.toggle('hidden', !specific);
        };

        document.getElementById('ov-cancel').onclick = closeModal;
        document.getElementById('ov-save').onclick = async () => {
            const name = document.getElementById('ov-name').value.trim();
            if (!name) {
                toast('Override name is required.', 'error');
                return;
            }
            const kind = parseInt(kindEl.value, 10);
            const payload = {
                name,
                kind,
                dayOfWeek: kind === 0 ? parseInt(document.getElementById('ov-day').value, 10) : null,
                specificDate: kind === 1 ? document.getElementById('ov-date').value : null,
                slots: Array.from({ length: 48 }, (_, i) => ({ slotIndex: i, candidates: [] }))
            };
            try {
                await api('/lineups/' + selectedChannelId + '/overrides', { method: 'POST', body: JSON.stringify(payload) });
                closeModal();
                toast('Override created.', 'success');
                await loadLineups();
            } catch (err) {
                toast(err.message, 'error');
            }
        };
    }

    async function deleteOverride(id) {
        if (!confirm('Delete this week lineup?')) return;
        try {
            await api('/lineups/overrides/' + id, { method: 'DELETE' });
            if (selectedLineupOverrideId === id) {
                selectedLineupOverrideId = null;
            }
            toast('Week lineup deleted.', 'success');
            await loadLineups();
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    function parseCommaList(value) {
        return (value || '').split(',').map((part) => part.trim()).filter(Boolean);
    }

    function parseDecadeList(value) {
        return parseCommaList(value)
            .map((part) => parseInt(part, 10))
            .filter((num) => Number.isFinite(num) && num >= 1900);
    }

    const COMMERCIALBRAINZ_DEFAULT_URL = 'https://commercialbrainz.org';

    function normalizeBrainzBaseUrl(value) {
        const trimmed = (value || '').trim().replace(/\/+$/, '');
        if (!trimmed) {
            return COMMERCIALBRAINZ_DEFAULT_URL;
        }

        try {
            const host = new URL(trimmed).hostname;
            if (host.toLowerCase() === 'commercialbrainz.duckdns.org') {
                return COMMERCIALBRAINZ_DEFAULT_URL;
            }
        } catch {
            return COMMERCIALBRAINZ_DEFAULT_URL;
        }

        return trimmed;
    }

    function readBrainzSettingsFromForm() {
        return {
            enabled: !!$('cb-enabled')?.checked,
            baseUrl: normalizeBrainzBaseUrl($('cb-base-url')?.value),
            apiToken: $('cb-api-token')?.value?.trim() || '',
            poolMode: parseInt($('cb-pool-mode')?.value, 10) || 2,
            maxSyncResults: parseInt($('cb-max-sync')?.value, 10) || 500,
            minYear: $('cb-min-year')?.value ? parseInt($('cb-min-year').value, 10) : null,
            maxYear: $('cb-max-year')?.value ? parseInt($('cb-max-year').value, 10) : null,
            decades: parseDecadeList($('cb-decades')?.value),
            brands: parseCommaList($('cb-brands')?.value),
            tags: parseCommaList($('cb-tags')?.value),
            excludeTags: parseCommaList($('cb-exclude-tags')?.value),
            genres: parseCommaList($('cb-genres')?.value),
            networks: parseCommaList($('cb-networks')?.value),
            channelNames: parseCommaList($('cb-channel-names')?.value),
            minAgeLimit: $('cb-min-age')?.value ? parseInt($('cb-min-age').value, 10) : null,
            maxAgeLimit: $('cb-max-age')?.value ? parseInt($('cb-max-age').value, 10) : null,
            allowSpoof: !!$('cb-allow-spoof')?.checked,
            allowFake: !!$('cb-allow-fake')?.checked,
            allowReal: !!$('cb-allow-real')?.checked,
            allowAiEnhanced: !!$('cb-allow-ai')?.checked,
            allowLateNight: !!$('cb-allow-latenight')?.checked,
            allowAdultRated: !!$('cb-allow-adult')?.checked,
            allowBanned: !!$('cb-allow-banned')?.checked
        };
    }

    function applyBrainzSettings(settings) {
        settings = settings || {};
        if ($('cb-enabled')) $('cb-enabled').checked = !!settings.enabled;
        if ($('cb-base-url')) {
            $('cb-base-url').value = normalizeBrainzBaseUrl(settings.baseUrl);
        }
        if ($('cb-api-token')) $('cb-api-token').value = '';
        if ($('cb-pool-mode')) $('cb-pool-mode').value = String(settings.poolMode ?? 2);
        if ($('cb-max-sync')) $('cb-max-sync').value = settings.maxSyncResults || 500;
        if ($('cb-min-year')) $('cb-min-year').value = settings.minYear ?? '';
        if ($('cb-max-year')) $('cb-max-year').value = settings.maxYear ?? '';
        if ($('cb-decades')) $('cb-decades').value = (settings.decades || []).join(', ');
        if ($('cb-brands')) $('cb-brands').value = (settings.brands || []).join(', ');
        if ($('cb-tags')) $('cb-tags').value = (settings.tags || []).join(', ');
        if ($('cb-exclude-tags')) $('cb-exclude-tags').value = (settings.excludeTags || []).join(', ');
        if ($('cb-genres')) $('cb-genres').value = (settings.genres || []).join(', ');
        if ($('cb-networks')) $('cb-networks').value = (settings.networks || []).join(', ');
        if ($('cb-channel-names')) $('cb-channel-names').value = (settings.channelNames || []).join(', ');
        if ($('cb-min-age')) $('cb-min-age').value = settings.minAgeLimit ?? '';
        if ($('cb-max-age')) $('cb-max-age').value = settings.maxAgeLimit ?? '';
        if ($('cb-allow-spoof')) $('cb-allow-spoof').checked = settings.allowSpoof !== false;
        if ($('cb-allow-fake')) $('cb-allow-fake').checked = settings.allowFake !== false;
        if ($('cb-allow-real')) $('cb-allow-real').checked = settings.allowReal !== false;
        if ($('cb-allow-ai')) $('cb-allow-ai').checked = settings.allowAiEnhanced !== false;
        if ($('cb-allow-latenight')) $('cb-allow-latenight').checked = settings.allowLateNight !== false;
        if ($('cb-allow-adult')) $('cb-allow-adult').checked = !!settings.allowAdultRated;
        if ($('cb-allow-banned')) $('cb-allow-banned').checked = !!settings.allowBanned;
        renderBrainzStatus(settings.syncState, settings.hasApiToken);
    }

    function renderBrainzStatus(syncState, hasApiToken) {
        const el = $('brainz-status');
        if (!el) return;
        const state = syncState || {};
        el.textContent = [
            `API token saved: ${hasApiToken ? 'yes' : 'no'}`,
            `Sync running: ${state.isRunning ? 'yes' : 'no'}`,
            `Last matched: ${state.lastMatchedCount ?? 0}`,
            `Last fetched: ${state.lastFetchedCount ?? 0}`,
            `Library count: ${state.libraryCount ?? 0}`,
            state.lastCompletedAt ? `Last sync: ${state.lastCompletedAt}` : 'Last sync: never',
            state.lastError ? `Last error: ${state.lastError}` : ''
        ].filter(Boolean).join('\n');
    }

    function formatDuration(seconds) {
        const total = Math.max(0, Number(seconds) || 0);
        const mins = Math.floor(total / 60);
        const secs = Math.floor(total % 60);
        return `${mins}:${String(secs).padStart(2, '0')}`;
    }

    function renderBrainzPreview(preview) {
        const el = $('brainz-preview');
        if (!el) return;
        if (!preview) {
            el.innerHTML = '';
            return;
        }

        const samples = preview.samples || preview.Samples || [];
        const matched = preview.matchedCount ?? preview.MatchedCount ?? 0;
        const fetched = preview.fetchedCount ?? preview.FetchedCount ?? 0;
        const enabled = preview.enabled !== false && preview.Enabled !== false;
        const shown = samples.length;
        const extra = Math.max(0, matched - shown);
        let banner = extra > 0
            ? `Showing ${shown} of at least <strong>${matched}</strong> matching commercials (${fetched} scanned).`
            : `Sync will pull <strong>${matched}</strong> commercial${matched === 1 ? '' : 's'} from ${fetched} scanned videos.`;
        if (!enabled) {
            banner += ' CommercialBrainz is currently disabled — enable it to sync these spots.';
        }

        if (!shown) {
            el.innerHTML = `<div class="preview-banner">${banner}</div><div class="empty-state">No commercials match the current filters.</div>`;
            return;
        }

        el.innerHTML = `<div class="preview-banner">${banner}</div>
            <div class="brainz-preview-grid">${samples.map((item) => {
                const title = item.title || item.Title || 'Commercial';
                const brand = item.brand || item.Brand || '';
                const year = item.year ?? item.Year;
                const duration = formatDuration(item.durationSeconds ?? item.DurationSeconds);
                const youtubeUrl = item.youtubeUrl || item.youTubeUrl || item.YouTubeUrl || '';
                const youtubeId = item.youtubeVideoId || item.youTubeVideoId || item.YouTubeVideoId || '';
                const pageUrl = item.commercialPageUrl || item.CommercialPageUrl || '';
                const thumb = youtubeId
                    ? resolveUrl('ChannelFlow/api/commercials/brainz/thumbnail/' + encodeURIComponent(youtubeId))
                    : (item.thumbnailUrl || item.ThumbnailUrl || '');
                const meta = [brand, year, duration].filter((part) => part !== '' && part != null).join(' · ');
                const thumbHtml = thumb
                    ? `<img src="${escapeHtml(thumb)}" alt="" loading="lazy" onerror="this.style.display='none'">`
                    : '';
                const pageInner = `<div class="thumb">${thumbHtml}</div><div class="body"><div class="title">${escapeHtml(title)}</div><div class="meta">${escapeHtml(meta)}</div></div>`;
                const pageLink = pageUrl
                    ? `<a class="brainz-preview-link" href="${escapeHtml(pageUrl)}" target="_blank" rel="noopener">${pageInner}</a>`
                    : `<div class="brainz-preview-link">${pageInner}</div>`;
                const youtubeBtn = youtubeUrl
                    ? `<a class="brainz-yt-btn" href="${escapeHtml(youtubeUrl)}" target="_blank" rel="noopener">YouTube</a>`
                    : '';
                return `<div class="brainz-preview-card">${pageLink}${youtubeBtn}</div>`;
            }).join('')}</div>`;
    }

    async function loadBrainzSettings() {
        try {
            const settings = await api('/commercials/brainz/settings');
            applyBrainzSettings(settings);
        } catch (err) {
            applyBrainzSettings(null);
            reportApiError(err, 'Could not load CommercialBrainz settings.');
        }
    }

    async function saveBrainzSettings(options = {}) {
        if (!syncConfigPage()) {
            toast('ChannelFlow-Server is not ready. Reload the page.', 'error');
            return;
        }

        try {
            const payload = readBrainzSettingsFromForm();
            const saved = await api('/commercials/brainz/settings', {
                method: 'PUT',
                body: JSON.stringify(payload)
            });
            applyBrainzSettings(saved);
            if (!options.silent) {
                toast('CommercialBrainz connection saved.', 'success');
            }
        } catch (err) {
            reportApiError(err, 'Could not save CommercialBrainz settings.');
        }
    }

    function readBrainzPlaylistPayload(name) {
        const settings = readBrainzSettingsFromForm();
        return {
            name,
            maxResults: Math.min(500, Math.max(1, settings.maxSyncResults || 50)),
            minYear: settings.minYear,
            maxYear: settings.maxYear,
            decades: settings.decades,
            brands: settings.brands,
            tags: settings.tags,
            excludeTags: settings.excludeTags,
            genres: settings.genres,
            networks: settings.networks,
            channelNames: settings.channelNames,
            minAgeLimit: settings.minAgeLimit,
            maxAgeLimit: settings.maxAgeLimit,
            allowSpoof: settings.allowSpoof,
            allowFake: settings.allowFake,
            allowReal: settings.allowReal,
            allowAiEnhanced: settings.allowAiEnhanced,
            allowLateNight: settings.allowLateNight,
            allowAdultRated: settings.allowAdultRated,
            allowBanned: settings.allowBanned
        };
    }

    function bindSavePlaylistMode() {
        const nameInput = $('brainz-new-playlist-name');
        const existingSelect = $('brainz-existing-playlist');
        const setMode = (mode) => {
            const isNew = mode === 'new';
            if ($('brainz-save-mode-new')) $('brainz-save-mode-new').checked = isNew;
            if ($('brainz-save-mode-existing')) $('brainz-save-mode-existing').checked = !isNew;
            if (nameInput) nameInput.disabled = !isNew;
            if (existingSelect) existingSelect.disabled = isNew || !commercialSearchPlaylists.length;
        };
        if ($('brainz-save-mode-new')) {
            $('brainz-save-mode-new').onchange = () => setMode('new');
        }
        if ($('brainz-save-mode-existing')) {
            $('brainz-save-mode-existing').onchange = () => setMode('existing');
        }
        if (nameInput) {
            nameInput.onfocus = () => setMode('new');
        }
        if (existingSelect) {
            existingSelect.onchange = () => setMode('existing');
        }
        setMode(commercialSearchPlaylists.length ? 'existing' : 'new');
        if (!commercialSearchPlaylists.length) {
            setMode('new');
        } else {
            setMode('new');
        }
    }

    async function openSavePlaylistModal() {
        if (!syncConfigPage()) {
            toast('ChannelFlow-Server is not ready. Reload the page.', 'error');
            return;
        }

        try {
            commercialSearchPlaylists = await api('/commercials/search-playlists') || commercialSearchPlaylists || [];
        } catch (err) {
            commercialSearchPlaylists = commercialSearchPlaylists || [];
        }

        const options = commercialSearchPlaylists.map((p) =>
            `<option value="${escapeHtml(p.id)}">${escapeHtml(p.name)}</option>`).join('');
        const hasExisting = commercialSearchPlaylists.length > 0;
        openModal('Save filters to playlist', `
            <div class="save-playlist-modal">
                <p class="hint">Anything entered in the filter fields is stored on the playlist and used when that playlist is assigned to a channel and you go to commercial.</p>
                <label class="field radio-field">
                    <input type="radio" name="brainz-save-mode" id="brainz-save-mode-new" value="new" checked>
                    <span>Create a new playlist</span>
                </label>
                <label class="field"><span>New playlist name</span>
                    <input id="brainz-new-playlist-name" class="emby-input" placeholder="80s toys">
                </label>
                <label class="field radio-field">
                    <input type="radio" name="brainz-save-mode" id="brainz-save-mode-existing" value="existing"${hasExisting ? '' : ' disabled'}>
                    <span>Save to an existing playlist</span>
                </label>
                <label class="field"><span>Existing playlist</span>
                    <select id="brainz-existing-playlist" class="emby-select"${hasExisting ? '' : ' disabled'}>
                        ${hasExisting ? options : '<option value="">No playlists yet</option>'}
                    </select>
                </label>
            </div>
        `, `
            <button type="button" class="emby-button" id="btn-cancel-playlist-save">Cancel</button>
            <button type="button" class="raised button-submit emby-button" id="btn-confirm-playlist-save">Save playlist</button>
        `);
        bindSavePlaylistMode();
        if ($('btn-cancel-playlist-save')) {
            $('btn-cancel-playlist-save').onclick = closeModal;
        }
        if ($('btn-confirm-playlist-save')) {
            $('btn-confirm-playlist-save').onclick = () => confirmSavePlaylist();
        }
    }

    function searchPlaylistPullKey(id) {
        return id == null ? '' : String(id);
    }

    function isSearchPlaylistPullInFlight(id) {
        const key = searchPlaylistPullKey(id);
        return !!key && searchPlaylistPullsInFlight.has(key);
    }

    function updateSearchPlaylistPullButtons() {
        document.querySelectorAll('.btn-pull-cb-playlist').forEach((btn) => {
            btn.disabled = isSearchPlaylistPullInFlight(btn.dataset.id);
        });
    }

    function confirmSavePlaylist() {
        if (searchPlaylistSaveBusy) {
            return;
        }

        const mode = $('brainz-save-mode-existing')?.checked ? 'existing' : 'new';
        const existingId = $('brainz-existing-playlist')?.value || '';
        const name = $('brainz-new-playlist-name')?.value?.trim() || '';
        if (mode === 'new' && !name) {
            toast('Enter a name for the new playlist.', 'error');
            return;
        }
        if (mode === 'existing' && !existingId) {
            toast('Pick an existing playlist.', 'error');
            return;
        }
        if (mode === 'existing' && isSearchPlaylistPullInFlight(existingId)) {
            toast('That playlist is already pulling matching spots.');
            return;
        }

        const existing = commercialSearchPlaylists.find((p) => String(p.id) === String(existingId));
        const payload = readBrainzPlaylistPayload(mode === 'new' ? name : (existing?.name || name));
        const saveBtn = $('btn-confirm-playlist-save');
        if (saveBtn) {
            saveBtn.disabled = true;
        }
        searchPlaylistSaveBusy = true;
        closeModal();
        toast('Saving playlist and copying matching spots into the Commercials table…');
        void savePlaylistAndPullInBackground(mode, existingId, payload);
    }

    async function savePlaylistAndPullInBackground(mode, existingId, payload) {
        try {
            await saveBrainzSettings({ silent: true });
            const saved = mode === 'existing'
                ? await api('/commercials/search-playlists/' + existingId, { method: 'PUT', body: JSON.stringify(payload) })
                : await api('/commercials/search-playlists', { method: 'POST', body: JSON.stringify(payload) });
            selectedSearchPlaylistId = saved.id;
            searchPlaylistSaveBusy = false;
            try {
                await loadSearchPlaylists();
            } catch (refreshErr) {
                reportApiError(refreshErr, 'Could not refresh saved playlists.');
            }
            await pullSearchPlaylist(saved.id, {
                startedToast: false,
                successToast: mode === 'existing'
                    ? `Saved “${saved.name}” and stored {count} spots in the Commercials table.`
                    : `Created “${saved.name}” and stored {count} spots in the Commercials table.`
            });
        } catch (err) {
            toast(err.message, 'error');
        } finally {
            searchPlaylistSaveBusy = false;
        }
    }

    async function previewBrainz(options = {}) {
        const el = $('brainz-preview');
        if (!options.skipSave) {
            await saveBrainzSettings({ silent: true });
        }
        if (el) {
            el.innerHTML = '<div class="empty-state">Loading commercial preview…</div>';
        }
        const preview = await api('/commercials/brainz/preview', { method: 'POST' });
        renderBrainzPreview(preview);
        if (!options.silent) {
            toast(`Preview: ${preview.matchedCount ?? preview.MatchedCount ?? 0} commercials will be synced`, 'success');
        }
    }

    async function syncBrainz() {
        await saveBrainzSettings();
        await api('/commercials/brainz/sync', { method: 'POST' });
        toast('CommercialBrainz sync started.', 'success');
        const previewEl = $('brainz-preview');
        if (previewEl) {
            previewEl.dataset.loaded = '';
        }
        await loadCommercialBrainz();
    }

    function isBrainzCommercial(item) {
        return item.source === 1 || item.source === 'CommercialBrainz';
    }

    function commercialDurationSeconds(item) {
        const duration = item.duration;
        if (typeof duration === 'number') {
            return duration;
        }
        return Math.round((duration && duration.totalSeconds) || 0);
    }

    function renderCommercialTable(targetId, list, emptyMessage) {
        const el = $(targetId);
        if (!el) {
            return;
        }
        if (!list || !list.length) {
            el.innerHTML = `<div class="empty-state">${emptyMessage}</div>`;
            return;
        }

        el.innerHTML = `<table class="data-table">
            <thead><tr><th>Title</th><th>Brand</th><th>Duration</th><th>Year</th><th>Chapters</th></tr></thead>
            <tbody>${list.map((c) => `<tr>
                <td>${escapeHtml(c.title)}</td>
                <td>${escapeHtml(c.brand || '')}</td>
                <td>${commercialDurationSeconds(c)}s</td>
                <td>${escapeHtml(String(c.year ?? ''))}</td>
                <td>${(c.chapters || []).length}</td>
            </tr>`).join('')}</tbody></table>`;
    }

    async function loadCommercials() {
        try {
            const list = await api('/commercials');
            const status = await api('/commercials/scan-status');
            if ($('commercial-status')) {
                $('commercial-status').textContent = status ? JSON.stringify(status, null, 2) : 'No scan running.';
            }
            const jellyfin = (list || []).filter((c) => !isBrainzCommercial(c));
            renderCommercialTable('commercial-list', jellyfin, 'No Jellyfin commercials synced yet. Tag items with channelflow-commercial and click Sync Jellyfin Library.');
            await loadSearchPlaylists();
            if (commercialsPage === 'map') {
                await loadChannelPlaylistMap();
            }
        } catch (err) {
            reportApiError(err, 'Could not load commercials.');
        }
    }

    async function loadSearchPlaylists() {
        const listEl = $('cb-playlist-list');
        if (!listEl) {
            return;
        }

        try {
            commercialSearchPlaylists = await api('/commercials/search-playlists') || [];
            if (!commercialSearchPlaylists.length) {
                selectedSearchPlaylistId = null;
                listEl.innerHTML = '<div class="empty-state">No saved playlists yet. Save filters from the CommercialBrainz tab.</div>';
                if ($('cb-playlist-items')) $('cb-playlist-items').innerHTML = '';
                refreshMappedPlaylistViews();
                return;
            }

            if (!selectedSearchPlaylistId || !commercialSearchPlaylists.some((p) => p.id === selectedSearchPlaylistId)) {
                selectedSearchPlaylistId = commercialSearchPlaylists[0].id;
            }

            listEl.innerHTML = `<table class="data-table">
                <thead><tr><th>Name</th><th>Spots</th><th>Last pull</th><th></th></tr></thead>
                <tbody>${commercialSearchPlaylists.map((p) => {
                    const selected = p.id === selectedSearchPlaylistId ? ' class="selected"' : '';
                    const synced = (p.lastSyncedAt || p.lastSyncedAt) ? new Date(p.lastSyncedAt || p.lastSyncedAt).toLocaleString() : 'never';
                    const err = (p.lastError || p.lastError) ? ` title="${escapeHtml(p.lastError || p.lastError)}"` : '';
                    const summary = p.filterSummary || p.query || '';
                    return `<tr data-playlist-id="${escapeHtml(p.id)}"${selected}${err}>
                        <td>${escapeHtml(p.name)}${summary ? `<span class="playlist-filter-sub">${escapeHtml(summary)}</span>` : ''}</td>
                        <td>${p.itemCount ?? p.itemCount ?? p.lastMatchedCount ?? p.lastMatchedCount ?? 0}</td>
                        <td>${escapeHtml((p.lastError || p.lastError) ? 'error' : synced)}</td>
                        <td>
                            <button type="button" class="emby-button btn-pull-cb-playlist" data-id="${escapeHtml(p.id)}"${isSearchPlaylistPullInFlight(p.id) ? ' disabled' : ''}>Pull</button>
                            <button type="button" class="emby-button btn-delete-cb-playlist" data-id="${escapeHtml(p.id)}">Delete</button>
                        </td>
                    </tr>`;
                }).join('')}</tbody></table>`;

            listEl.querySelectorAll('tbody tr').forEach((row) => {
                row.onclick = (event) => {
                    if (event.target.closest('button')) {
                        return;
                    }
                    selectedSearchPlaylistId = row.dataset.playlistId;
                    listEl.querySelectorAll('tbody tr').forEach((r) => r.classList.toggle('selected', r.dataset.playlistId === selectedSearchPlaylistId));
                    renderSearchPlaylistItems();
                };
            });
            listEl.querySelectorAll('.btn-pull-cb-playlist').forEach((btn) => {
                btn.onclick = (event) => {
                    event.stopPropagation();
                    void pullSearchPlaylist(btn.dataset.id);
                };
            });
            listEl.querySelectorAll('.btn-delete-cb-playlist').forEach((btn) => {
                btn.onclick = (event) => {
                    event.stopPropagation();
                    deleteSearchPlaylist(btn.dataset.id);
                };
            });
            renderSearchPlaylistItems();
            refreshMappedPlaylistViews();
        } catch (err) {
            reportApiError(err, 'Could not load saved playlists.');
        }
    }

    function renderSearchPlaylistItems() {
        const el = $('cb-playlist-items');
        if (!el) {
            return;
        }
        const playlist = commercialSearchPlaylists.find((p) => p.id === selectedSearchPlaylistId);
        if (!playlist) {
            el.innerHTML = '';
            return;
        }
        const items = playlist.items || [];
        if (!items.length) {
            el.innerHTML = `<p class="playlist-sample-caption">Sample of spots this playlist will pull into commercial breaks.</p>
                <div class="empty-state">Click Pull to see a sample of matching spots for “${escapeHtml(playlist.name)}”.</div>`;
            return;
        }
        el.innerHTML = `<p class="playlist-sample-caption">Sample of spots this playlist will pull into commercial breaks${playlist.filterSummary ? ` · ${escapeHtml(playlist.filterSummary)}` : ''}.</p>
            <table class="data-table">
            <thead><tr><th>${escapeHtml(playlist.name)}</th><th>Brand</th><th>Year</th><th>Duration</th></tr></thead>
            <tbody>${items.map((c) => {
                const title = c.youtubeUrl
                    ? `<a href="${escapeHtml(c.youtubeUrl)}" target="_blank" rel="noopener">${escapeHtml(c.title)}</a>`
                    : escapeHtml(c.title);
                return `<tr>
                    <td>${title}</td>
                    <td>${escapeHtml(c.brand || '')}</td>
                    <td>${escapeHtml(String(c.year ?? ''))}</td>
                    <td>${c.durationSeconds ?? 0}s</td>
                </tr>`;
            }).join('')}</tbody></table>`;
    }

    async function pullSearchPlaylist(id, options = {}) {
        if (!id) {
            return;
        }
        const key = searchPlaylistPullKey(id);
        if (searchPlaylistPullsInFlight.has(key)) {
            return;
        }

        searchPlaylistPullsInFlight.add(key);
        updateSearchPlaylistPullButtons();
        if (options.startedToast !== false) {
            toast('Refreshing matching spots from CommercialBrainz…');
        }

        try {
            const playlist = await api('/commercials/search-playlists/' + id + '/pull', { method: 'POST' });
            selectedSearchPlaylistId = playlist.id;
            const count = playlist.itemCount ?? playlist.lastMatchedCount ?? 0;
            const successTemplate = options.successToast;
            toast(
                successTemplate
                    ? successTemplate.replace('{count}', String(count))
                    : `Stored ${count} spots for “${playlist.name}”.`,
                'success'
            );
            await loadCommercials();
        } catch (err) {
            toast(err.message, 'error');
            try {
                await loadSearchPlaylists();
            } catch (refreshErr) {
                reportApiError(refreshErr, 'Could not refresh saved playlists.');
            }
        } finally {
            searchPlaylistPullsInFlight.delete(key);
            updateSearchPlaylistPullButtons();
        }
    }

    async function deleteSearchPlaylist(id) {
        if (!confirm('Delete this saved playlist? Synced commercials stay in the library.')) {
            return;
        }
        try {
            await api('/commercials/search-playlists/' + id, { method: 'DELETE' });
            if (selectedSearchPlaylistId === id) {
                selectedSearchPlaylistId = null;
            }
            toast('Playlist deleted.', 'success');
            await loadSearchPlaylists();
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function loadCommercialBrainz() {
        await loadBrainzSettings();
        const previewEl = $('brainz-preview');
        if (previewEl && previewEl.dataset.loaded !== '1') {
            previewEl.dataset.loaded = '1';
            try {
                await previewBrainz({ skipSave: true, silent: true });
            } catch (previewErr) {
                previewEl.dataset.loaded = '';
                previewEl.innerHTML = `<div class="empty-state">${escapeHtml(previewErr.message || 'Could not load commercial preview.')}</div>`;
            }
        }
    }

    const SPONSORBLOCK_LABELS = {
        sponsor: 'Sponsor',
        selfpromo: 'Self-promo',
        interaction: 'Interaction reminders',
        intro: 'Intro',
        outro: 'Outro',
        preview: 'Preview / recap',
        hook: 'Hook',
        filler: 'Filler',
        music_offtopic: 'Non-music section'
    };

    function renderYouTubeStatus(settings) {
        const el = $('youtube-status');
        if (!el) {
            return;
        }

        const ytDlp = settings.ytDlpAvailable ? 'yt-dlp found' : 'yt-dlp missing';
        const cookiePath = settings.cookieFilePath ? ` at ${settings.cookieFilePath}` : '';
        const cookies = settings.hasCookies
            ? (settings.looksSignedIn
                ? `cookies saved (${settings.cookieCount} rows, looks signed in)${cookiePath}`
                : `cookies saved (${settings.cookieCount} rows)${cookiePath}`)
            : `no cookies saved${cookiePath}`;
        const premium = settings.preferPremium ? 'Premium formats on' : 'Premium formats off';
        const sb = settings.sponsorBlockEnabled ? 'SponsorBlock on' : 'SponsorBlock off';
        el.textContent = `${ytDlp} · ${cookies} · ${premium} · ${sb}`;
    }

    function renderSponsorBlockCategories(known, selected) {
        const host = $('yt-sb-categories');
        if (!host) {
            return;
        }

        const selectedSet = new Set(selected || []);
        host.innerHTML = (known || Object.keys(SPONSORBLOCK_LABELS)).map((id) => {
            const label = SPONSORBLOCK_LABELS[id] || id;
            const checked = selectedSet.has(id) ? ' checked' : '';
            return `<label class="field checkbox-field">
                <input type="checkbox" data-sb-category="${escapeHtml(id)}"${checked}>
                <span class="channelflow-check-box" aria-hidden="true"></span>
                <span>${escapeHtml(label)}</span>
            </label>`;
        }).join('');
    }

    function readSponsorBlockCategories() {
        return Array.from(qa('#yt-sb-categories input[data-sb-category]:checked')).map((el) => el.dataset.sbCategory);
    }

    function applyYouTubeSettings(settings) {
        settings = settings || {};
        if ($('yt-prefer-premium')) {
            $('yt-prefer-premium').checked = settings.preferPremium !== false;
        }
        if ($('yt-sponsorblock')) {
            $('yt-sponsorblock').checked = settings.sponsorBlockEnabled !== false;
        }
        if ($('yt-cookies')) {
            $('yt-cookies').value = '';
        }
        const fileEl = $('yt-cookies-file');
        if (fileEl) {
            fileEl.value = '';
        }
        renderSponsorBlockCategories(settings.knownCategories, settings.sponsorBlockCategories);
        renderYouTubeStatus(settings);
    }

    async function loadYouTube() {
        try {
            const settings = await api('/youtube/settings');
            applyYouTubeSettings(settings);
        } catch (err) {
            reportApiError(err, 'Could not load YouTube settings.');
        }
    }

    async function saveYouTubeSettings(options = {}) {
        if (!syncConfigPage()) {
            toast('ChannelFlow-Server is not ready. Reload the page.', 'error');
            return;
        }

        try {
            const pasted = ($('yt-cookies')?.value || '').trim();
            const saved = await api('/youtube/settings', {
                method: 'PUT',
                body: JSON.stringify({
                    cookies: pasted || null,
                    preferPremium: !!$('yt-prefer-premium')?.checked,
                    sponsorBlockEnabled: !!$('yt-sponsorblock')?.checked,
                    sponsorBlockCategories: readSponsorBlockCategories()
                })
            });
            applyYouTubeSettings(saved);
            if (!options.silent) {
                if (saved.hasCookies) {
                    toast(saved.looksSignedIn
                        ? `YouTube cookies saved (${saved.cookieCount} rows, signed in).`
                        : `YouTube cookies saved (${saved.cookieCount} rows).`, 'success');
                } else if (pasted) {
                    toast('YouTube settings saved, but cookies were not kept. Paste a Netscape cookies.txt export.', 'error');
                } else {
                    toast('YouTube settings saved.', 'success');
                }
            }
        } catch (err) {
            reportApiError(err, 'Could not save YouTube settings.');
        }
    }

    async function clearYouTubeCookies() {
        if (!confirm('Remove the saved YouTube cookies from this server?')) {
            return;
        }

        const saved = await api('/youtube/settings', {
            method: 'PUT',
            body: JSON.stringify({ clearCookies: true })
        });
        applyYouTubeSettings(saved);
        toast('YouTube cookies cleared.', 'success');
    }

    async function testYouTubeAccess() {
        const resultEl = $('youtube-test-result');
        if (resultEl) {
            resultEl.textContent = 'Testing yt-dlp…';
        }
        try {
            await saveYouTubeSettings({ silent: true });
            const result = await api('/youtube/test', { method: 'POST' });
            const text = result.message || (result.ok ? 'YouTube access works.' : 'YouTube test failed.');
            if (resultEl) {
                resultEl.textContent = text;
            }
            toast(text, result.ok ? 'success' : 'error');
        } catch (err) {
            if (resultEl) {
                resultEl.textContent = err.message || 'YouTube test failed.';
            }
            reportApiError(err, 'YouTube test failed.');
        }
    }

    async function loadPresets() {
        try {
            presetNumberingMode = parseInt($('preset-numbering-mode').value, 10) || 0;
            channelPresets = await api('/channels/presets?numberingMode=' + presetNumberingMode) || [];
            renderPresetsList();
        } catch (err) {
            reportApiError(err, 'Could not load presets.');
        }
    }

    function renderPresetsList() {
        const el = $('presets-list');
        if (!channelPresets.length) {
            el.innerHTML = '<div class="empty-state">No presets available.</div>';
            return;
        }

        const altLabel = presetNumberingMode === 1 ? 'Legacy #' : 'Sub #';
        const categories = [...new Set(channelPresets.map((p) => p.category))];
        el.innerHTML = categories.map((category) => {
            const rows = channelPresets.filter((p) => p.category === category);
            return `<h4 style="margin:1rem 0 .5rem">${escapeHtml(category)}</h4>
                <table class="data-table">
                    <thead><tr><th>#</th><th>${altLabel}</th><th>Name</th><th>Description</th><th>Library Tag</th><th>Status</th></tr></thead>
                    <tbody>${rows.map((p) => `<tr>
                        <td><strong>${formatChannelNumber(p.number)}</strong></td>
                        <td>${formatChannelNumber(presetNumberingMode === 1 ? p.legacyNumber : p.subchannelNumber)}</td>
                        <td>${escapeHtml(p.name)}</td>
                        <td>${escapeHtml(p.description)}</td>
                        <td><code>${escapeHtml(p.libraryTag)}</code></td>
                        <td><span class="badge ${p.exists ? 'badge-on' : 'badge-off'}">${p.exists ? 'Exists' : 'Missing'}</span></td>
                    </tr>`).join('')}</tbody>
                </table>`;
        }).join('');
    }

    async function applyPresets() {
        if (!syncConfigPage()) {
            toast('ChannelFlow-Server is not ready. Reload the page.', 'error');
            return;
        }

        const btn = $('btn-apply-presets');
        const resultEl = $('presets-result');
        const numberingEl = $('preset-numbering-mode');
        const updateExistingEl = $('preset-update-existing');
        const originalLabel = btn ? btn.textContent : '';

        if (!numberingEl) {
            toast('Preset controls are not loaded. Switch to the Presets tab and try again.', 'error');
            return;
        }

        try {
            if (btn) {
                btn.disabled = true;
                btn.textContent = 'Creating…';
            }

            const updateExisting = !!updateExistingEl?.checked;
            presetNumberingMode = parseInt(numberingEl.value, 10) || 0;
            const result = await api('/channels/presets/apply', {
                method: 'POST',
                body: JSON.stringify({
                    numberingMode: presetNumberingMode,
                    skipExisting: !updateExisting,
                    updateExisting: updateExisting
                })
            });
            const lines = [];
            if (result?.created?.length) {
                lines.push(`Created ${result.created.length}: ${result.created.map((r) => formatChannelNumber(r.number) + ' ' + r.name).join(', ')}`);
            }
            if (result?.updated?.length) {
                lines.push(`Updated ${result.updated.length}: ${result.updated.map((r) => formatChannelNumber(r.number) + ' ' + r.name).join(', ')}`);
            }
            if (result?.skipped?.length) {
                lines.push(`Skipped ${result.skipped.length} existing channel(s).`);
            }
            if (resultEl) {
                resultEl.classList.remove('hidden');
                resultEl.textContent = lines.join('\n') || 'No changes made — all preset channels already exist.';
            }
            toast(lines[0] || 'All preset channels already exist.', lines.length ? 'success' : 'info');
            await loadPresets();
            await loadChannels();
        } catch (err) {
            reportApiError(err, 'Could not apply channel presets.');
        } finally {
            if (btn) {
                btn.disabled = false;
                btn.textContent = originalLabel || 'Create Missing Channels';
            }
        }
    }

    function updateEbsLibraryFieldVisibility() {
        const source = Number($('ebs-music-source')?.value || $('setup-ebs-music-source')?.value || '2');
        const audioMode = Number($('ebs-audio-mode')?.value || '0');
        const field = $('ebs-library-field') || $('setup-ebs-library-field');
        if (field) field.style.display = audioMode === 0 && source === 1 ? '' : 'none';
        const hint = $('ebs-music-source-hint');
        if (hint) hint.style.display = audioMode === 0 ? '' : 'none';
        const packsCard = $('ebs-music-packs-card');
        if (packsCard) packsCard.style.display = audioMode === 0 ? '' : 'none';
    }

    let musicPackPoll = null;

    async function loadMusicPacks() {
        const el = $('ebs-music-packs');
        if (!el) {
            return;
        }

        try {
            const data = await api('/music-packs');
            renderMusicPacks(data.packs || []);
            const downloading = (data.packs || []).some((pack) => pack.status === 'downloading');
            if (downloading) {
                startMusicPackPoll();
            } else {
                stopMusicPackPoll();
            }
        } catch (err) {
            el.innerHTML = `<div class="empty-state">${escapeHtml(err.message || 'Could not load music packs.')}</div>`;
            stopMusicPackPoll();
        }
    }

    function startMusicPackPoll() {
        if (musicPackPoll) {
            return;
        }
        musicPackPoll = setInterval(() => {
            loadMusicPacks().catch(() => {});
        }, 1500);
    }

    function stopMusicPackPoll() {
        if (musicPackPoll) {
            clearInterval(musicPackPoll);
            musicPackPoll = null;
        }
    }

    function renderMusicPacks(packs) {
        const el = $('ebs-music-packs');
        if (!el) {
            return;
        }
        if (!packs.length) {
            el.innerHTML = '<div class="empty-state">No music packs in the catalog.</div>';
            return;
        }
        el.innerHTML = packs.map(renderMusicPackCard).join('');
    }

    function renderMusicPackCard(pack) {
        const version = pack.installedVersion
            ? `Installed v${pack.installedVersion}` + (pack.status === 'updateAvailable' ? ` · catalog v${pack.catalogVersion}` : '')
            : `Catalog v${pack.catalogVersion}`;
        const tracks = pack.trackCount
            ? `${pack.trackCount} track${pack.trackCount === 1 ? '' : 's'}`
            : 'Not downloaded';
        const active = pack.isActive ? '<span class="stat-pill">Playing now</span>' : '';
        const skipped = pack.autoDownloadSkipped
            ? '<p class="hint">Removed. ChannelFlow will not auto-download this pack until you click Download.</p>'
            : '';
        const error = pack.error ? `<p class="hint">${escapeHtml(pack.error)}</p>` : '';
        let actions = '';
        if (pack.status === 'downloading') {
            actions = '<button type="button" class="emby-button" disabled>Downloading…</button>';
        } else if (!pack.hasDriveFile) {
            actions = '<button type="button" class="emby-button" disabled>Coming soon</button>';
        } else if (pack.status === 'updateAvailable') {
            actions = `<button type="button" class="raised button-submit emby-button" data-pack-download="${escapeHtml(pack.id)}">Update to v${escapeHtml(pack.catalogVersion)}</button>
                <button type="button" class="emby-button" data-pack-remove="${escapeHtml(pack.id)}">Remove</button>`;
        } else if (pack.trackCount > 0) {
            actions = `<button type="button" class="emby-button" data-pack-remove="${escapeHtml(pack.id)}">Remove</button>`;
        } else {
            actions = `<button type="button" class="raised button-submit emby-button" data-pack-download="${escapeHtml(pack.id)}">Download</button>`;
        }
        return `<div class="ebs-upload-card">
            <h4>${escapeHtml(pack.name || pack.id)} ${active}</h4>
            <p class="hint">${escapeHtml(pack.playsWhen || '')}</p>
            <p class="hint">${escapeHtml(version)} · ${escapeHtml(tracks)}</p>
            ${error}${skipped}
            <div class="actions">${actions}</div>
        </div>`;
    }

    async function downloadMusicPack(id) {
        await api('/music-packs/' + encodeURIComponent(id) + '/download', { method: 'POST' });
        toast('Downloading music pack…', 'info');
        await loadMusicPacks();
    }

    async function removeMusicPack(id) {
        const anytime = String(id || '').toLowerCase() === 'anytime';
        const message = anytime
            ? 'Remove Anytime? ChannelFlow will not download it again until you click Download.'
            : 'Remove the downloaded files for this music pack? You can download it again later.';
        if (!confirm(message)) {
            return;
        }
        await api('/music-packs/' + encodeURIComponent(id), { method: 'DELETE' });
        toast('Music pack removed.', 'success');
        await loadMusicPacks();
    }

    function updateEbsFieldVisibility() {
        const displayMode = Number($('ebs-display-mode')?.value || '0');
        const slateVariantField = $('ebs-slate-variant-field');
        const slateVariantHint = $('ebs-slate-variant-hint');
        const musicSourceField = $('ebs-music-source')?.closest('.field');
        const audioMode = Number($('ebs-audio-mode')?.value || '0');

        if (slateVariantField) {
            slateVariantField.style.display = displayMode === 0 ? '' : 'none';
        }
        if (slateVariantHint) {
            slateVariantHint.style.display = displayMode === 0 ? '' : 'none';
        }
        if (musicSourceField) {
            musicSourceField.style.display = audioMode !== 0 ? 'none' : '';
        }
        updateEbsLibraryFieldVisibility();
        refreshEbsPreviews();
    }

    function setEbsPreviewImage(img, url) {
        if (!img) return;
        const wrap = img.closest('.ebs-upload-preview-wrap');
        img.onload = () => {
            img.classList.remove('hidden');
            wrap?.classList.remove('hidden');
        };
        img.onerror = () => {
            img.classList.add('hidden');
            img.removeAttribute('src');
            wrap?.classList.add('hidden');
        };
        img.classList.add('hidden');
        img.src = url;
    }

    function setEbsLiveSlateImage(img, missing, url) {
        if (!img) return;
        img.onload = () => {
            img.classList.remove('hidden');
            if (missing) {
                missing.classList.add('hidden');
                missing.hidden = true;
            }
        };
        img.onerror = () => {
            img.classList.add('hidden');
            img.removeAttribute('src');
            if (missing) {
                missing.classList.remove('hidden');
                missing.hidden = false;
            }
        };
        img.classList.add('hidden');
        img.src = url;
    }

    function refreshEbsPreviews() {
        const displayMode = Number($('ebs-display-mode')?.value || '0');
        const variant = Number($('ebs-slate-variant')?.value || '0');
        const bust = Date.now();
        const liveImg = $('ebs-live-preview-image');
        const liveCaption = $('ebs-live-preview-caption');
        const bars = $('ebs-live-preview-bars');
        const snow = $('ebs-live-preview-static');
        const missing = $('ebs-live-preview-missing');
        const pair = $('ebs-live-preview-pair');
        const generatedFrame = $('ebs-live-preview-frame');

        bars?.classList.toggle('hidden', displayMode !== 1);
        snow?.classList.toggle('hidden', displayMode !== 2);
        if (bars) bars.hidden = displayMode !== 1;
        if (snow) snow.hidden = displayMode !== 2;
        pair?.classList.toggle('hidden', displayMode !== 0);
        generatedFrame?.classList.toggle('hidden', displayMode === 0);

        if (displayMode === 0) {
            if (liveCaption) {
                liveCaption.textContent = variant === 1
                    ? 'World stills during dead air and playback errors. 16:9 channels use the wide still; 4:3 channels use the 4:3 still.'
                    : 'USA stills during dead air and playback errors. 16:9 channels use the wide still; 4:3 channels use the 4:3 still.';
            }
            if (missing) {
                missing.classList.add('hidden');
                missing.hidden = true;
            }
            if (liveImg) {
                liveImg.classList.add('hidden');
                liveImg.removeAttribute('src');
            }
            setEbsLiveSlateImage(
                $('ebs-live-preview-image-169'),
                $('ebs-live-preview-missing-169'),
                withAppBase('/api/ebs/preview?variant=' + variant + '&aspect=0&t=' + bust)
            );
            setEbsLiveSlateImage(
                $('ebs-live-preview-image-43'),
                $('ebs-live-preview-missing-43'),
                withAppBase('/api/ebs/preview?variant=' + variant + '&aspect=1&t=' + bust)
            );
        } else {
            ['ebs-live-preview-image-169', 'ebs-live-preview-image-43'].forEach((id) => {
                const img = $(id);
                if (img) {
                    img.classList.add('hidden');
                    img.removeAttribute('src');
                }
            });
            ['ebs-live-preview-missing-169', 'ebs-live-preview-missing-43'].forEach((id) => {
                const el = $(id);
                if (el) {
                    el.classList.add('hidden');
                    el.hidden = true;
                }
            });
            if (liveImg) {
                liveImg.classList.add('hidden');
                liveImg.removeAttribute('src');
            }
            if (missing) {
                missing.classList.add('hidden');
                missing.hidden = true;
            }
            if (liveCaption) {
                liveCaption.textContent = displayMode === 1
                    ? 'Viewers see generated color bars during dead air and playback errors.'
                    : 'Viewers see generated TV static during dead air and playback errors.';
            }
        }

        setEbsPreviewImage($('ebs-usa-preview'), withAppBase('/api/ebs/slates/usa/image?aspect=0&t=' + bust));
        setEbsPreviewImage($('ebs-international-preview'), withAppBase('/api/ebs/slates/international/image?aspect=0&t=' + bust));
    }

    function populateEbsMusicLibraries(libraries, selectedId, selectedName, selectId) {
        const select = $(selectId || 'ebs-music-library') || $('setup-ebs-music-library');
        if (!select) return;

        const items = libraries || [];
        if (items.length === 0) {
            select.innerHTML = '<option value="">No music libraries found in Jellyfin</option>';
            return;
        }

        const options = items.map((lib) => {
            const id = lib.id ?? lib.Id ?? '';
            const name = lib.name ?? lib.Name ?? 'Music library';
            return `<option value="${escapeHtml(String(id))}">${escapeHtml(String(name))}</option>`;
        });
        select.innerHTML = options.join('');

        if (selectedId && [...select.options].some((opt) => opt.value === selectedId)) {
            select.value = selectedId;
            return;
        }

        const byName = [...select.options].find((opt) => opt.textContent === selectedName);
        if (byName) {
            select.value = byName.value;
        }
    }

    function renderEbsCustomSlateStatus(customSlates) {
        const usa = customSlates?.usa;
        const international = customSlates?.international;
        const usaEl = $('ebs-usa-status');
        const intlEl = $('ebs-international-status');
        if (usaEl) {
            usaEl.textContent = usa?.fileName
                ? `Custom upload: ${usa.fileName}`
                : 'Using bundled 16:9 and 4:3 stills.';
        }
        if (intlEl) {
            intlEl.textContent = international?.fileName
                ? `Custom upload: ${international.fileName}`
                : 'Using bundled 16:9 and 4:3 stills.';
        }
    }

    const AI_CATALOG_MODES = ['TV only', 'Movies only', 'Both', 'Music videos only'];
    function buildAiApplyPayload() {
        const source = aiPreview?.lineupSlots || aiPreview?.LineupSlots;
        if (!Array.isArray(source) || source.length === 0) {
            throw new Error('No AI lineup to apply. Generate a lineup first.');
        }

        const slots = source.map((slot) => ({
            SlotIndex: slot.slotIndex ?? slot.SlotIndex,
            SpanSlots: Math.max(1, Math.min(8, Number(slot.spanSlots ?? slot.SpanSlots ?? 1))),
            IsRerunSlot: !!(slot.isRerunSlot ?? slot.IsRerunSlot),
            Candidates: (slot.candidates || slot.Candidates || []).map((c, index) => ({
                Kind: resolveEnumValue(SLOT_CANDIDATE_KIND_VALUES, c.kind ?? c.Kind, 0),
                JellyfinItemId: c.jellyfinItemId ?? c.JellyfinItemId ?? null,
                CollectionName: c.collectionName ?? c.CollectionName ?? null,
                FilterJson: c.filterJson ?? c.FilterJson ?? null,
                Weight: Number(c.weight ?? c.Weight ?? 1),
                SortOrder: Number(c.sortOrder ?? c.SortOrder ?? index)
            }))
        }));

        return { Slots: slots, RebuildPlayout: true };
    }

    function updateAiUiState() {
        const enabled = $('ai-enabled') ? !!$('ai-enabled').checked : !!(aiSettings && aiSettings.enabled);
        const note = $('ai-disabled-note');
        note?.classList.toggle('hidden', enabled);
        qa('.ai-action').forEach((el) => { el.disabled = !enabled; });
        qa('.ai-channel-row').forEach((row) => row.classList.toggle('disabled-row', !enabled));
        if ($('btn-ai-generate-all')) $('btn-ai-generate-all').disabled = !enabled;
        if ($('ai-auto-apply-channel-add')) $('ai-auto-apply-channel-add').disabled = !enabled;
        if ($('ai-auto-apply-all-on-save')) $('ai-auto-apply-all-on-save').disabled = !enabled;
    }

    function defaultAiProviderDraft() {
        return {
            0: { model: 'gpt-4o-mini', key: null, keyEdited: false },
            1: { model: 'gpt-4o-mini', key: null, keyEdited: false }
        };
    }

    function ensureAiProviderDraft() {
        if (!aiProviderDraft) {
            aiProviderDraft = defaultAiProviderDraft();
        }
    }

    function selectedAiProviderId() {
        return Number($('ai-default-provider')?.value || '0') === 1 ? 1 : 0;
    }

    function maskedKeyForProvider(providerId) {
        if (!aiSettings) {
            return '';
        }

        return providerId === 1 ? aiSettings.veniceApiKeyMasked : aiSettings.openAiApiKeyMasked;
    }

    function stashAiProviderFields(providerId) {
        ensureAiProviderDraft();
        const draft = aiProviderDraft[providerId === 1 ? 1 : 0];
        const modelEl = $('ai-model');
        const keyEl = $('ai-api-key');
        if (modelEl) {
            draft.model = modelEl.value.trim() || 'gpt-4o-mini';
        }

        if (keyEl && keyEl.dataset.userEdited === '1') {
            const key = normalizeApiKeyInput(keyEl.value);
            const masked = maskedKeyForProvider(providerId);
            if (key && !looksLikeMaskedApiKey(key) && key !== masked) {
                draft.key = key;
                draft.keyEdited = true;
            }
        }
    }

    function applyAiProviderFields() {
        ensureAiProviderDraft();
        const providerId = aiVisibleProviderId;
        const venice = providerId === 1;
        const draft = aiProviderDraft[providerId];
        const keyLabel = $('ai-api-key-label');
        const modelLabel = $('ai-model-label');
        if (keyLabel) {
            keyLabel.textContent = venice ? 'Venice API key' : 'OpenAI API key';
        }

        if (modelLabel) {
            modelLabel.textContent = venice ? 'Venice model' : 'OpenAI model';
        }

        const modelEl = $('ai-model');
        if (modelEl) {
            modelEl.value = draft.model || 'gpt-4o-mini';
        }

        const keyEl = $('ai-api-key');
        if (keyEl) {
            keyEl.name = venice ? 'cf-ai-venice-token' : 'cf-ai-openai-token';
            if (draft.keyEdited && draft.key) {
                keyEl.value = draft.key;
                keyEl.dataset.userEdited = '1';
                keyEl.readOnly = false;
            } else {
                keyEl.value = '';
                delete keyEl.dataset.userEdited;
                keyEl.readOnly = true;
            }
        }

        const keyStatus = $('ai-key-status');
        if (keyStatus && aiSettings) {
            keyStatus.textContent = venice
                ? `Venice key: ${aiSettings.hasVeniceApiKey ? aiSettings.veniceApiKeyMasked : 'not set'}`
                : `OpenAI key: ${aiSettings.hasOpenAiApiKey ? aiSettings.openAiApiKeyMasked : 'not set'}`;
        }
    }

    function onAiProviderChange() {
        stashAiProviderFields(aiVisibleProviderId);
        aiVisibleProviderId = selectedAiProviderId();
        applyAiProviderFields();
    }

    function readAiSettingsFromForm() {
        stashAiProviderFields(aiVisibleProviderId);
        ensureAiProviderDraft();
        return {
            enabled: !!$('ai-enabled')?.checked,
            autoApplyOnChannelAdd: !!$('ai-auto-apply-channel-add')?.checked,
            autoApplyToAllChannelsOnSave: !!$('ai-auto-apply-all-on-save')?.checked,
            simulateOriginalBroadcasting: !!$('ai-simulate-original-broadcasting')?.checked,
            defaultProvider: selectedAiProviderId(),
            openAiModel: aiProviderDraft[0].model || 'gpt-4o-mini',
            veniceModel: aiProviderDraft[1].model || 'gpt-4o-mini',
            ttsModel: $('ai-tts-model')?.value?.trim() || '',
            ttsVoice: $('ai-tts-voice')?.value?.trim() || 'nova',
            openAiApiKey: aiProviderDraft[0].keyEdited ? aiProviderDraft[0].key : null,
            veniceApiKey: aiProviderDraft[1].keyEdited ? aiProviderDraft[1].key : null
        };
    }

    function looksLikeMaskedApiKey(value) {
        const key = String(value || '');
        if (key === '****' || /^[.*•]+$/.test(key)) {
            return true;
        }

        return key.includes('...') && key.length <= 16;
    }

    function bindAiApiKeyFields() {
        const el = $('ai-api-key');
        if (!el || el.dataset.guardBound === '1') {
            return;
        }

        el.dataset.guardBound = '1';
        el.readOnly = true;
        el.addEventListener('focus', () => {
            el.readOnly = false;
        });
        el.addEventListener('keydown', (event) => {
            if (event.metaKey || event.ctrlKey || event.altKey) {
                return;
            }
            if (event.key.length === 1 || event.key === 'Backspace' || event.key === 'Delete') {
                el.dataset.userEdited = '1';
            }
        });
        el.addEventListener('paste', () => {
            el.dataset.userEdited = '1';
        });
        el.addEventListener('input', (event) => {
            const type = event.inputType || '';
            if (type === 'insertText' || type === 'insertFromPaste' || type === 'insertFromDrop' || type === 'insertFromYank') {
                el.dataset.userEdited = '1';
            }
        });
    }

    async function loadAi() {
        if (!syncConfigPage()) {
            return;
        }

        try {
            aiSettings = await api('/ai/settings');
            if ($('ai-enabled')) $('ai-enabled').checked = !!aiSettings.enabled;
            if ($('ai-auto-apply-channel-add')) $('ai-auto-apply-channel-add').checked = !!aiSettings.autoApplyOnChannelAdd;
            if ($('ai-auto-apply-all-on-save')) $('ai-auto-apply-all-on-save').checked = !!aiSettings.autoApplyToAllChannelsOnSave;
            if ($('ai-simulate-original-broadcasting')) $('ai-simulate-original-broadcasting').checked = !!aiSettings.simulateOriginalBroadcasting;
            if ($('ai-default-provider')) $('ai-default-provider').value = String(aiSettings.defaultProvider ?? 0);
            aiProviderDraft = defaultAiProviderDraft();
            aiProviderDraft[0].model = aiSettings.openAiModel || 'gpt-4o-mini';
            aiProviderDraft[1].model = aiSettings.veniceModel || 'gpt-4o-mini';
            aiVisibleProviderId = selectedAiProviderId();
            applyAiProviderFields();
            if ($('ai-tts-model')) $('ai-tts-model').value = aiSettings.ttsModel || '';
            if ($('ai-tts-voice')) $('ai-tts-voice').value = aiSettings.ttsVoice || 'nova';
            aiChannels = await api('/ai/channels');
            aiPlayoutTemplates = await api('/ai/playout-templates');
            renderAiChannels();
            populatePrimetimeChannelSelect();
            updateAiUiState();
            const job = await api('/ai/generate-all/status');
            renderGenerateAllStatus(job);
            if (job.isRunning) {
                startGenerateAllPolling();
                refreshAiGeneratePopup(job);
            }
            await loadWeatherGuideCacheStatus();
            if (aiPage === 'primetime') {
                await loadPrimetimeEditor();
            }
        } catch (err) {
            reportApiError(err, 'Could not load AI settings.');
        }
    }

    let weatherGuideCachePollTimer = null;

    function renderWeatherGuideCacheStatus(status) {
        const el = $('ai-weather-guide-cache-status');
        const genBtn = $('btn-weather-guide-cache-generate');
        const clearBtn = $('btn-weather-guide-cache-clear');
        if (!el || !status) {
            return;
        }

        if (status.isGenerating) {
            el.textContent =
                `Generating weather guide cache… ${status.completeChannels}/${status.channelCount} channel(s) complete · ${status.entryCount} hour slot(s) cached.`;
            if (genBtn) {
                genBtn.disabled = true;
                genBtn.textContent = 'Generating…';
            }
            if (clearBtn) {
                clearBtn.disabled = true;
            }
            return;
        }

        if (genBtn) {
            genBtn.disabled = false;
            genBtn.textContent = 'Generate Weather Guide Cache';
        }
        if (clearBtn) {
            clearBtn.disabled = false;
        }

        if (!status.channelCount) {
            el.textContent = 'No enabled weather channels. Add a weather channel to build guide metadata.';
            return;
        }

        const dateLabel = status.forecastDate ? ` for ${status.forecastDate}` : '';
        const source = status.weatherSource ? ` Source: ${status.weatherSource}.` : '';
        const generated = status.lastGeneratedAt
            ? ` Last built ${new Date(status.lastGeneratedAt).toLocaleString()}.`
            : '';
        let line =
            `${status.completeChannels}/${status.channelCount} weather channel(s) have today's forecast${dateLabel} · ${status.entryCount} cached hour(s).${source}${generated}`;
        const partial = (status.channels || []).filter((c) => c.hoursCached > 0 && !c.isComplete);
        if (partial.length) {
            const names = partial.map((c) => `${c.channelName} (${c.hoursCached}/24)`).join(', ');
            line += ` Partial: ${names}.`;
        } else if (status.completeChannels < status.channelCount) {
            line += " Click Generate Weather Guide Cache to fill today's hours from the Weather tab source.";
        } else {
            line += ' Auto-refreshes at local midnight.';
        }

        el.textContent = line;
    }

    function stopWeatherGuideCachePolling() {
        if (weatherGuideCachePollTimer) {
            clearTimeout(weatherGuideCachePollTimer);
            weatherGuideCachePollTimer = null;
        }
    }

    function startWeatherGuideCachePolling() {
        stopWeatherGuideCachePolling();
        weatherGuideCachePollTimer = setTimeout(pollWeatherGuideCacheStatus, 3000);
    }

    async function loadWeatherGuideCacheStatus() {
        try {
            const status = await api('/ai/weather-guide-cache/status');
            renderWeatherGuideCacheStatus(status);
            if (status.isGenerating) {
                startWeatherGuideCachePolling();
            } else {
                stopWeatherGuideCachePolling();
            }
        } catch (err) {
            const el = $('ai-weather-guide-cache-status');
            if (el) {
                el.textContent = 'Could not load weather guide cache status.';
            }
        }
    }

    async function pollWeatherGuideCacheStatus() {
        try {
            const status = await api('/ai/weather-guide-cache/status');
            renderWeatherGuideCacheStatus(status);
            if (status.isGenerating) {
                startWeatherGuideCachePolling();
            } else {
                stopWeatherGuideCachePolling();
            }
        } catch (err) {
            stopWeatherGuideCachePolling();
        }
    }

    async function generateWeatherGuideCache(force = true) {
        const result = await api('/ai/weather-guide-cache/generate', {
            method: 'POST',
            body: JSON.stringify({ force })
        });

        if (result.alreadyRunning) {
            toast('Weather guide cache generation is already running.', 'info');
        } else if (result.queued) {
            toast('Weather guide cache generation started.', 'success');
        }

        renderWeatherGuideCacheStatus(result.status);
        if (result.status?.isGenerating) {
            startWeatherGuideCachePolling();
        }
    }

    async function clearWeatherGuideCache() {
        if (!confirm('Delete all cached weather guide metadata? EPG will use fallback titles until you generate a new cache.')) {
            return;
        }

        const result = await api('/ai/weather-guide-cache', { method: 'DELETE' });
        toast(`Cleared ${result.cleared} weather guide cache entries.`, 'success');
        await loadWeatherGuideCacheStatus();
    }

    let aiGenerateAllPollTimer = null;
    let aiGenerateAllLastCompletedSteps = null;
    let aiGenerateAllIdlePolls = 0;

    function renderGenerateAllStatus(job) {
        const el = $('ai-generate-all-status');
        const cancelBtn = $('btn-ai-cancel-generate-all');
        if (!el || !job) {
            el?.classList.add('hidden');
            cancelBtn?.classList.add('hidden');
            return;
        }

        if (!job.isRunning && !job.completedAt) {
            el.classList.add('hidden');
            cancelBtn?.classList.add('hidden');
            return;
        }

        el.classList.remove('hidden');
        if (job.isRunning) {
            const totalSteps = job.totalSteps || 0;
            const pct = totalSteps ? Math.round((job.completedSteps / totalSteps) * 100) : 0;
            let statusLine;
            if (job.currentPhase === 'generating' || job.currentPhase === 'lineup') {
                statusLine =
                    `Generate all: ${job.currentChannelName || '…'} · generating lineup · ` +
                    `${job.completedSteps}/${totalSteps || '?'} steps (${pct}%)`;
            } else if (job.currentPhase === 'priming' || (job.currentPhase || '').startsWith('priming')) {
                statusLine =
                    `Generate all: ${job.currentChannelName || '…'} · priming page ${job.page || 1}/${job.totalPages || '?'} · ` +
                    `${job.completedSteps}/${totalSteps || '?'} steps (${pct}%)`;
            } else if (job.currentPhase === 'queuing') {
                statusLine = `Generate all: queuing channels…`;
            } else if (job.currentPhase === 'horizon-full') {
                statusLine =
                    `Generate all: ${job.currentChannelName || '…'} · 14-day guide already filled (next day at midnight) · ` +
                    `${job.completedSteps}/${totalSteps || '?'} steps (${pct}%)`;
            } else {
                statusLine =
                    `Generate all: ${job.currentChannelName || '…'} · day ${job.currentDay || 1}/${job.totalDays || 14} playout · ` +
                    `then next day, then next channel · ${job.completedSteps}/${totalSteps || '?'} steps (${pct}%)`;
            }
            if (job.workerActive === false) {
                statusLine += ' · no background worker (stale — click Cancel to reset)';
            } else if (aiGenerateAllIdlePolls >= 6) {
                statusLine += ' · no recent progress (may be waiting on AI — click Cancel to stop)';
            }
            el.textContent = statusLine;
            if ($('btn-ai-generate-all')) {
                $('btn-ai-generate-all').disabled = true;
                $('btn-ai-generate-all').textContent = 'Generating…';
            }
            cancelBtn?.classList.remove('hidden');
            if (cancelBtn) {
                cancelBtn.disabled = false;
            }
            return;
        }

        cancelBtn?.classList.add('hidden');

        if (job.wasCancelled) {
            el.textContent =
                `Generate all cancelled after ${job.lineupsGenerated} lineup(s) and ${job.playoutDaysBuilt} playout day(s).`;
        } else if (job.wasStale) {
            el.textContent =
                `Generate all stopped at ${job.completedSteps}/${job.totalSteps || '?'} steps. ${job.lastError || 'Background task is no longer running.'}`;
        } else {
            let message = `Generate all finished: ${job.lineupsGenerated} lineups, ${job.playoutDaysBuilt} playout days built across ${job.totalChannels} channel(s) and ${job.totalDays} day(s).`;
            if (job.lineupsFailed || job.playoutDaysFailed) {
                message += ` Failures: ${job.lineupsFailed} lineup, ${job.playoutDaysFailed} day.`;
            }
            if (job.lastError) {
                message += ` Last error: ${job.lastError}`;
            }
            el.textContent = message;
        }

        if ($('btn-ai-generate-all')) {
            $('btn-ai-generate-all').disabled = !($('ai-enabled')?.checked);
            $('btn-ai-generate-all').textContent = 'Generate All Channels';
        }
    }

    function stopGenerateAllPolling() {
        if (aiGenerateAllPollTimer) {
            clearTimeout(aiGenerateAllPollTimer);
            aiGenerateAllPollTimer = null;
        }
    }

    function startGenerateAllPolling() {
        if (aiGenerateAllPollTimer) {
            clearTimeout(aiGenerateAllPollTimer);
        }
        aiGenerateAllPollTimer = setTimeout(pollGenerateAllStatus, 3000);
    }

    async function pollGenerateAllStatus() {
        try {
            const job = await api('/ai/generate-all/status');
            if (job.isRunning) {
                if (aiGenerateAllLastCompletedSteps === job.completedSteps) {
                    aiGenerateAllIdlePolls++;
                } else {
                    aiGenerateAllLastCompletedSteps = job.completedSteps;
                    aiGenerateAllIdlePolls = 0;
                }
            } else {
                aiGenerateAllLastCompletedSteps = null;
                aiGenerateAllIdlePolls = 0;
            }

            renderGenerateAllStatus(job);
            if (job.isRunning || job.completedAt) {
                refreshAiGeneratePopup(job);
            }
            if (job.isRunning) {
                if (isGuideTabVisible()) {
                    loadGuide({ quiet: true }).catch(() => {});
                }
                startGenerateAllPolling();
                return;
            }

            stopGenerateAllPolling();

            if (job.completedAt) {
                if (job.wasStale) {
                    toast(job.lastError || 'Generate all is no longer running. Status was reset.', 'info');
                } else if (job.wasCancelled) {
                    toast(
                        `Generate all cancelled after ${job.lineupsGenerated} lineup(s) and ${job.playoutDaysBuilt} playout day(s).`,
                        'info'
                    );
                } else {
                    toast(
                        `Generate all finished: ${job.lineupsGenerated} lineups and ${job.playoutDaysBuilt} playout days built.`,
                        job.lineupsFailed || job.playoutDaysFailed ? 'info' : 'success'
                    );
                }
                await loadAi();
                await refreshGuide();
            }
        } catch (_) {
            startGenerateAllPolling();
        }
    }

    async function cancelGenerateAll() {
        const cancelBtn = $('btn-ai-cancel-generate-all');
        try {
            if (cancelBtn) {
                cancelBtn.disabled = true;
            }

            const data = await api('/ai/generate-all/cancel', { method: 'POST', body: '{}' });
            if (data.cancelled) {
                toast('Cancel requested. Generate all will stop after the current channel/day step.', 'info');
            } else {
                toast('Generate all is not running. Status reset if it was stale.', 'info');
            }

            renderGenerateAllStatus(data.job);
            if (!data.job?.isRunning) {
                stopGenerateAllPolling();
            } else {
                startGenerateAllPolling();
            }
        } catch (err) {
            toast(err.message, 'error');
        } finally {
            if (cancelBtn && $('ai-generate-all-status')?.textContent?.includes('Generate all:')) {
                cancelBtn.disabled = false;
            }
        }
    }

    function renderAiChannels() {
        const list = $('ai-channels-list');
        if (!list) return;
        if (!aiChannels.length) {
            list.innerHTML = '<div class="empty-state">No channels available for AI lineup generation.</div>';
            return;
        }

        list.innerHTML = aiChannels.map((ch) => {
            const mix = ch.catalogMode == null ? 2 : Number(ch.catalogMode);
            const templateOptions = (aiPlayoutTemplates || []).map((t) =>
                `<option value="${escapeHtml(t.id)}" ${(ch.aiPlayoutTemplateId || 'none') === t.id ? 'selected' : ''}>${escapeHtml(t.name)}</option>`
            ).join('');
            return `
            <div class="ai-channel-row" data-ai-channel="${ch.id}">
                <div class="row-top">
                    <div>
                        <strong>${escapeHtml(ch.number)} · ${escapeHtml(ch.name)}</strong>
                        <div class="meta">${escapeHtml(ch.libraryTag || 'no tag')} · ${ch.filledSlots}/48 slots filled · ${ch.poolCount || 0} in AI pool</div>
                    </div>
                    <div class="row-actions">
                        <label class="field" style="margin:0">
                            <span>Content mix</span>
                            <select class="emby-input ai-catalog-mode" data-channel="${ch.id}">
                                <option value="0" ${mix === 0 ? 'selected' : ''}>TV only</option>
                                <option value="1" ${mix === 1 ? 'selected' : ''}>Movies only</option>
                                <option value="2" ${mix === 2 ? 'selected' : ''}>Both</option>
                                <option value="3" ${mix === 3 ? 'selected' : ''}>Music videos only</option>
                            </select>
                        </label>
                        <label class="field" style="margin:0">
                            <span>Playout template</span>
                            <select class="emby-input ai-playout-template ai-template-select" data-channel="${ch.id}">${templateOptions}</select>
                        </label>
                        <button type="button" class="emby-button ai-action ai-save-channel" data-channel="${ch.id}">Save</button>
                        <button type="button" class="emby-button ai-action ai-generate-channel" data-channel="${ch.id}">Generate</button>
                        <button type="button" class="emby-button ai-action ai-clear-pool" data-channel="${ch.id}">Clear AI pool</button>
                        <button type="button" class="emby-button ai-action ai-clear-playout" data-channel="${ch.id}">Clear playout</button>
                    </div>
                </div>
                ${ch.aiRuleBrief ? `<p class="hint">${escapeHtml(ch.aiRuleBrief)}</p>` : ''}
                <p class="hint">Both means TV plus Prime Time movies. Early Bird / next-day reruns encore last night's TV series only — movies are not repeated.</p>
                <p class="hint ai-channel-generate-status hidden"></p>
                <label class="field">
                    <span>Fine-tune prompt</span>
                    <textarea class="emby-input ai-fine-tune" data-channel="${ch.id}" placeholder="Optional extra instructions for this channel">${escapeHtml(ch.aiFineTunePrompt || '')}</textarea>
                </label>
            </div>`;
        }).join('');

        list.querySelectorAll('.ai-save-channel').forEach((btn) => {
            btn.onclick = () => saveAiChannelSettings(btn.dataset.channel);
        });
        list.querySelectorAll('.ai-generate-channel').forEach((btn) => {
            btn.onclick = () => generateAiLineup(btn.dataset.channel);
        });
        list.querySelectorAll('.ai-clear-pool').forEach((btn) => {
            btn.onclick = () => clearAiChannelPool(btn.dataset.channel);
        });
        list.querySelectorAll('.ai-clear-playout').forEach((btn) => {
            btn.onclick = () => clearAiChannelPlayout(btn.dataset.channel);
        });
        updateAiUiState();
    }

    async function saveAiSettings() {
        if (!syncConfigPage()) {
            toast('ChannelFlow-Server is not ready. Reload the page.', 'error');
            return;
        }

        const btn = $('btn-save-ai-settings');
        const originalLabel = btn ? btn.textContent : '';
        try {
            if (btn) {
                btn.disabled = true;
                btn.textContent = 'Saving…';
            }

            const form = readAiSettingsFromForm();
            const payload = {
                enabled: form.enabled,
                autoApplyOnChannelAdd: form.autoApplyOnChannelAdd,
                autoApplyToAllChannelsOnSave: form.autoApplyToAllChannelsOnSave,
                simulateOriginalBroadcasting: form.simulateOriginalBroadcasting,
                defaultProvider: form.defaultProvider,
                openAiModel: form.openAiModel,
                veniceModel: form.veniceModel,
                ttsModel: form.ttsModel,
                ttsVoice: form.ttsVoice
            };
            if (form.openAiApiKey) payload.openAiApiKey = form.openAiApiKey;
            if (form.veniceApiKey) payload.veniceApiKey = form.veniceApiKey;
            const response = await api('/ai/settings', { method: 'PUT', body: JSON.stringify(payload) });
            aiSettings = response.settings || response;
            if (response.applyAll?.queued) {
                toast('AI settings saved. Apply-to-all is running in the background.', 'success');
            } else if (response.applyAll) {
                const summary = response.applyAll;
                toast(
                    `AI settings saved. Applied to ${summary.ok} channel(s)` +
                    (summary.failed ? `, ${summary.failed} failed` : '') +
                    (summary.skipped ? `, ${summary.skipped} skipped` : '') +
                    '.',
                    summary.failed ? 'info' : 'success'
                );
            } else {
                toast('AI settings saved.', 'success');
            }
            await loadAi();
        } catch (err) {
            reportApiError(err, 'Could not save AI settings.');
        } finally {
            if (btn) {
                btn.disabled = false;
                btn.textContent = originalLabel || 'Save AI Settings';
            }
        }
    }

    function normalizeApiKeyInput(value) {
        if (!value) {
            return '';
        }

        let key = String(value).trim().replace(/^['"]|['"]$/g, '');
        if (/^bearer\s+/i.test(key)) {
            key = key.replace(/^bearer\s+/i, '').trim();
        }

        return key;
    }

    async function testAiConnection() {
        if (!syncConfigPage()) {
            toast('ChannelFlow-Server is not ready. Reload the page.', 'error');
            return;
        }

        const btn = $('btn-test-ai');
        const originalLabel = btn ? btn.textContent : '';
        try {
            if (btn) {
                btn.disabled = true;
                btn.textContent = 'Testing…';
            }
            const form = readAiSettingsFromForm();
            const payload = {
                providerId: form.defaultProvider
            };
            if (form.openAiApiKey) payload.openAiApiKey = form.openAiApiKey;
            if (form.veniceApiKey) payload.veniceApiKey = form.veniceApiKey;
            const data = await api('/ai/settings/test', {
                method: 'POST',
                body: JSON.stringify(payload)
            });
            toast(`Connected to ${data.provider}.`, 'success');
        } catch (err) {
            toast((err && err.message) || 'AI connection test failed.', 'error');
        } finally {
            if (btn) {
                btn.disabled = false;
                btn.textContent = originalLabel || 'Test Connection';
            }
        }
    }

    async function saveAiChannelSettings(channelId) {
        if (!syncConfigPage()) {
            toast('ChannelFlow-Server is not ready. Reload the page.', 'error');
            return;
        }

        const row = q(`[data-ai-channel="${channelId}"]`);
        if (!row) return;
        try {
            await api('/ai/channels/' + channelId + '/fine-tune', {
                method: 'PUT',
                body: JSON.stringify({
                    aiFineTunePrompt: row.querySelector('.ai-fine-tune')?.value || '',
                    catalogMode: Number(row.querySelector('.ai-catalog-mode')?.value || '0'),
                    aiPlayoutTemplateId: row.querySelector('.ai-playout-template')?.value || 'none'
                })
            });
            toast('Channel AI settings saved.', 'success');
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    function renderChannelGenerateStatus(channelId, status) {
        const row = q(`[data-ai-channel="${channelId}"]`);
        const el = row?.querySelector('.ai-channel-generate-status');
        const btn = row?.querySelector('.ai-generate-channel');
        if (!el) {
            return;
        }

        if (!status || !status.isRunning) {
            el.classList.add('hidden');
            el.textContent = '';
            if (btn && aiSettings && aiSettings.enabled) {
                btn.textContent = 'Generate';
            }
            return;
        }

        el.classList.remove('hidden');
        const totalDays = status.totalDays || 14;
        if (status.phase === 'generating') {
            el.textContent = 'Generating lineup… then building playout day 1 of ' + totalDays + '.';
            if (btn) {
                btn.textContent = 'Generating…';
            }
            return;
        }

        if (status.phase === 'horizon-full') {
            el.textContent = 'Lineup saved. Guide already has ' + totalDays + ' days. The next day is built at midnight.';
            if (btn) {
                btn.textContent = 'Up to date';
            }
            return;
        }

        el.textContent = 'Lineup saved. Building guide day ' + (status.currentDay || 1) + ' of ' + totalDays + '…';
        if (btn) {
            btn.textContent = 'Day ' + (status.currentDay || 1) + '/' + totalDays;
        }
    }

    async function waitForChannelGenerate(channelId, maxWaitMs) {
        const started = Date.now();
        const timeoutMs = maxWaitMs || 1800000;
        let lineupSavedToast = false;
        while (Date.now() - started < timeoutMs) {
            const status = await api('/ai/channels/' + channelId + '/generate/status');
            renderChannelGenerateStatus(channelId, status);
            refreshAiGeneratePopup(status);
            if (status.applied && status.isRunning && !lineupSavedToast) {
                lineupSavedToast = true;
                toast('AI lineup saved. Building the TV guide one day at a time…', 'info');
            }

            if (isGuideTabVisible()) {
                loadGuide({ quiet: true }).catch(() => {});
            }

            if (!status.isRunning) {
                if (status.error) {
                    throw new Error(status.error);
                }

                if (!status.preview) {
                    throw new Error('AI lineup generation finished without a preview.');
                }

                return status;
            }

            await new Promise((resolve) => setTimeout(resolve, 3000));
        }

        throw new Error('AI lineup generation is taking longer than expected. Check the ChannelFlow log and try again.');
    }

    async function generateAiLineup(channelId) {
        if (!syncConfigPage()) {
            toast('ChannelFlow-Server is not ready. Reload the page.', 'error');
            return;
        }

        const row = q(`[data-ai-channel="${channelId}"]`);
        if (row) {
            await saveAiChannelSettings(channelId);
        }
        const btn = row?.querySelector('.ai-generate-channel');
        if (btn) btn.disabled = true;
        try {
            const start = await api('/ai/channels/' + channelId + '/generate', { method: 'POST', body: '{}' });
            if (start.alreadyRunning) {
                toast('AI lineup generation is already queued for this channel…', 'info');
            } else {
                toast('AI lineup generation queued…', 'info');
            }
            beginLibraryTaskPopup('ai-generate', 'AI lineup generator', mapAiGenerateTask(start.job) || {
                isRunning: true,
                message: 'Queued…'
            });

            const status = await waitForChannelGenerate(channelId);
            aiPreview = status.preview;
            renderAiPreview();
            if (status.applyError) {
                toast('AI lineup generated, but the Live TV guide was not rebuilt: ' + status.applyError, 'error');
            } else if (status.phase === 'horizon-full') {
                toast('AI lineup saved. Guide already has 14 days. The next day will be built at midnight.', 'success');
            } else {
                toast('AI lineup generated and Live TV guide rebuilt.', 'success');
            }
            await loadGuide({ quiet: true });
        } catch (err) {
            toast(err.message, 'error');
        } finally {
            renderChannelGenerateStatus(channelId, { isRunning: false });
            if (btn) {
                btn.disabled = !(aiSettings && aiSettings.enabled);
                btn.textContent = 'Generate';
            }
        }
    }

    function renderAiPreview() {
        const panel = $('ai-preview-panel');
        if (!aiPreview || !panel) return;
        panel.classList.remove('hidden');
        $('ai-preview-title').textContent = `Preview: ${aiPreview.channelName}`;
        const templateLabel = aiPreview.playoutTemplateName && aiPreview.playoutTemplateId !== 'none'
            ? ` · Template: ${aiPreview.playoutTemplateName}`
            : '';
        $('ai-preview-summary').textContent = `AI chose from ${aiPreview.catalogSummary.includedInPrompt} of ${aiPreview.catalogSummary.totalAvailable} tagged items · ${AI_CATALOG_MODES[aiPreview.catalogMode] || 'Mixed'} mode${templateLabel} · lineup is applied and the Live TV guide is rebuilt (up to 14 days)`;
        const grid = $('ai-preview-grid');
        const occupied = new Array(48).fill(false);
        const blocks = (aiPreview.slots || []).filter((s) =>
            isRerunSlot(s) || (s.title && s.title !== 'Filter fallback') || s.jellyfinItemId);
        let html = '';
        for (let i = 0; i < 48; i++) {
            if (occupied[i]) continue;
            const block = blocks.find((s) => s.slotIndex === i);
            if (!block) {
                html += `<div class="slot-card empty"><div class="time">${slotTime(i)}</div><div class="summary">Open</div></div>`;
                occupied[i] = true;
                continue;
            }
            const span = Math.max(1, block.spanSlots || 1);
            for (let j = i; j < i + span && j < 48; j++) occupied[j] = true;
            const duration = span * 30;
            const rerun = isRerunSlot(block);
            html += `<div class="slot-card ${rerun ? 'rerun-slot' : 'has-items'} span-block" style="--slot-span:${span};grid-column:span ${span}">
                <div class="time">${slotTime(i)} · ${duration}m</div>
                <div class="summary">${escapeHtml(block.title)}</div>
                <div class="count">${escapeHtml(block.type || '')}${block.runtimeMinutes ? ' · ' + block.runtimeMinutes + 'm' : ''}${block.daypartName ? `<span class="ai-daypart-badge">${escapeHtml(block.daypartName)}</span>` : ''}</div>
            </div>`;
        }
        grid.innerHTML = html;
        updateAiUiState();
    }

    async function applyAiLineup() {
        if (!aiPreview) return;
        try {
            await api('/ai/channels/' + aiPreview.channelId + '/apply', {
                method: 'POST',
                body: JSON.stringify(buildAiApplyPayload())
            });
            toast('Lineup applied. Rebuilding the Live TV guide…', 'info');
            await waitForPlayoutRebuild(aiPreview.channelId);
            discardAiPreview();
            await loadAi();
            await loadGuide({ quiet: true });
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    function discardAiPreview() {
        aiPreview = null;
        $('ai-preview-panel')?.classList.add('hidden');
        if ($('ai-preview-grid')) $('ai-preview-grid').innerHTML = '';
    }

    async function clearAiChannelPool(channelId) {
        if (!confirm('Clear this channel’s AI pool? Next Generate will re-page the live catalog.')) {
            return;
        }

        await api('/ai/channels/' + channelId + '/pool', { method: 'DELETE' });
        toast('AI pool cleared. Next Generate will rebuild it.', 'success');
        await loadAi();
    }

    async function clearAiChannelPlayout(channelId) {
        if (!confirm('Clear this channel’s Live TV playout? The guide goes empty and the channel goes Off Air until you Generate or Rebuild Playout. The lineup is kept.')) {
            return;
        }

        const data = await api('/lineups/' + channelId + '/clear-playout', { method: 'POST', body: '{}' });
        const count = Number(data.cleared || 0);
        const name = data.channelName || 'channel';
        toast(`Cleared ${count} playout item(s) for ${name}. Channel is Off Air until Generate or Rebuild.`, 'success');
        refreshGuide().catch(() => {});
    }

    function primetimeChannels() {
        return (aiChannels || []).filter((ch) => ch.primetimeAssignment);
    }

    function applyAiPage() {
        document.querySelectorAll('#ai-inner-tabs .inner-tab').forEach((tab) => {
            const on = tab.dataset.aiPage === aiPage;
            tab.classList.toggle('active', on);
            tab.setAttribute('aria-selected', on ? 'true' : 'false');
        });
        document.querySelectorAll('#tab-ai .ai-page').forEach((page) => {
            const on = page.id === 'ai-page-' + aiPage;
            page.classList.toggle('hidden', !on);
            page.hidden = !on;
        });
        if (aiPage === 'primetime') {
            loadPrimetimeEditor().catch((err) => toast(err.message, 'error'));
        }
    }

    function populatePrimetimeChannelSelect() {
        const select = $('ai-primetime-channel');
        if (!select) {
            return;
        }

        const channels = primetimeChannels();
        const previous = select.value;
        select.innerHTML = channels.map((ch) =>
            `<option value="${escapeHtml(ch.id)}">${escapeHtml(ch.number)} · ${escapeHtml(ch.name)}</option>`
        ).join('');
        if (channels.some((ch) => ch.id === previous)) {
            select.value = previous;
        }
        const empty = $('ai-primetime-empty');
        const editor = $('ai-primetime-editor');
        if (empty) {
            empty.classList.toggle('hidden', channels.length > 0);
        }
        if (editor) {
            editor.classList.toggle('hidden', channels.length === 0);
        }
    }

    async function loadPrimetimeEditor() {
        populatePrimetimeChannelSelect();
        const channelId = $('ai-primetime-channel')?.value;
        if (!channelId) {
            primetimeShows = [];
            primetimeSlots = [];
            renderPrimetimeEditor();
            return;
        }

        const [showsRes, slotsRes] = await Promise.all([
            api('/ai/channels/' + channelId + '/primetime-shows'),
            api('/ai/channels/' + channelId + '/primetime')
        ]);
        primetimeShows = showsRes.shows || [];
        primetimeSlots = (slotsRes.slots || []).map((slot) => ({
            slotIndex: slot.slotIndex,
            label: slot.label,
            candidates: (slot.candidates || []).map((c) => ({
                id: c.id,
                title: c.title
            }))
        }));
        if (!primetimeSlots.some((s) => s.slotIndex === primetimeSelectedSlot)) {
            primetimeSelectedSlot = primetimeSlots[0]?.slotIndex ?? 36;
        }
        renderPrimetimeEditor();
    }

    function renderPrimetimeEditor() {
        const list = $('ai-primetime-shows');
        const slotsEl = $('ai-primetime-slots');
        if (!list || !slotsEl) {
            return;
        }

        const query = (primetimeSearch || '').trim().toLowerCase();
        const filtered = primetimeShows.filter((show) => {
            if (!query) {
                return true;
            }
            const title = String(show.title || '').toLowerCase();
            const year = show.year != null ? String(show.year) : '';
            return title.includes(query) || year.includes(query);
        });
        list.innerHTML = filtered.length
            ? filtered.map((show) => {
                const year = show.year ? ` (${show.year})` : '';
                return `<button type="button" class="primetime-show" data-series="${escapeHtml(show.id)}">${escapeHtml(show.title)}${escapeHtml(year)}</button>`;
            }).join('')
            : '<div class="empty-state">No matching shows for this channel.</div>';
        list.querySelectorAll('.primetime-show').forEach((btn) => {
            btn.onclick = () => addShowToPrimetimeSlot(btn.dataset.series);
        });

        slotsEl.innerHTML = primetimeSlots.map((slot) => {
            const chips = (slot.candidates || []).map((c) =>
                `<span class="primetime-chip">${escapeHtml(c.title)}<button type="button" data-slot="${slot.slotIndex}" data-series="${escapeHtml(c.id)}" aria-label="Remove">&times;</button></span>`
            ).join('');
            const active = slot.slotIndex === primetimeSelectedSlot ? ' active' : '';
            return `<div class="primetime-slot${active}" data-slot="${slot.slotIndex}">
                <div class="primetime-slot-label">${escapeHtml(slot.label)}</div>
                <div class="primetime-chips">${chips || '<span class="hint">No shows yet</span>'}</div>
            </div>`;
        }).join('');
        slotsEl.querySelectorAll('.primetime-slot').forEach((el) => {
            el.onclick = (event) => {
                if (event.target.closest('button')) {
                    return;
                }
                primetimeSelectedSlot = Number(el.dataset.slot);
                renderPrimetimeEditor();
            };
        });
        slotsEl.querySelectorAll('.primetime-chip button').forEach((btn) => {
            btn.onclick = (event) => {
                event.stopPropagation();
                removeShowFromPrimetimeSlot(Number(btn.dataset.slot), btn.dataset.series);
            };
        });
    }

    function addShowToPrimetimeSlot(seriesId) {
        const show = primetimeShows.find((s) => s.id === seriesId);
        const slot = primetimeSlots.find((s) => s.slotIndex === primetimeSelectedSlot);
        if (!show || !slot) {
            return;
        }
        if (slot.candidates.some((c) => c.id === show.id)) {
            return;
        }
        slot.candidates.push({ id: show.id, title: show.title });
        renderPrimetimeEditor();
    }

    function removeShowFromPrimetimeSlot(slotIndex, seriesId) {
        const slot = primetimeSlots.find((s) => s.slotIndex === slotIndex);
        if (!slot) {
            return;
        }
        slot.candidates = slot.candidates.filter((c) => c.id !== seriesId);
        renderPrimetimeEditor();
    }

    async function savePrimetimeAssignments() {
        const channelId = $('ai-primetime-channel')?.value;
        if (!channelId) {
            toast('Pick a prime-TV channel first.', 'info');
            return;
        }

        const payload = {
            slots: primetimeSlots.map((slot) => ({
                slotIndex: slot.slotIndex,
                candidates: slot.candidates.map((c) => ({
                    seriesId: c.id,
                    title: c.title
                }))
            }))
        };
        const saved = await api('/ai/channels/' + channelId + '/primetime', {
            method: 'PUT',
            body: JSON.stringify(payload)
        });
        primetimeSlots = (saved.slots || primetimeSlots);
        toast('Primetime assignments saved. Generate to rebuild the guide.', 'success');
        renderPrimetimeEditor();
    }

    async function clearAllAiPools() {
        if (!confirm('Clear every channel’s AI pool? Next Generate will re-page the live catalog for each channel.')) {
            return;
        }

        await api('/ai/pools', { method: 'DELETE' });
        toast('AI pool cleared. Next Generate will rebuild it.', 'success');
        await loadAi();
    }

    async function generateAllAiLineups() {
        if (!confirm('Queue AI lineups for all eligible channels? Clicks stack into one batch. Two or more channels build day 1 for every channel, then day 2. This runs in the background.')) return;
        const btn = $('btn-ai-generate-all');
        const originalLabel = btn ? btn.textContent : '';
        try {
            if (btn) {
                btn.disabled = true;
                btn.textContent = 'Queueing…';
            }
            const data = await api('/ai/generate-all', { method: 'POST', body: '{}' });
            if (data.alreadyRunning) {
                toast('Generate all is already running.', 'info');
                renderGenerateAllStatus(data.job);
                startGenerateAllPolling();
                beginLibraryTaskPopup('ai-generate', 'AI lineup generator', mapAiGenerateTask(data.job));
                return;
            }
            if (data.queued) {
                toast('Generate all queued.', 'success');
                renderGenerateAllStatus(data.job);
                startGenerateAllPolling();
                beginLibraryTaskPopup('ai-generate', 'AI lineup generator', mapAiGenerateTask(data.job) || { isRunning: true, message: 'Queuing…' });
                return;
            }
            const failed = (data.results || []).filter((r) => !r.ok && !r.skipped);
            const ok = (data.results || []).filter((r) => r.ok).length;
            const fail = failed.length;
            const skipped = (data.results || []).filter((r) => r.skipped).length;
            let message = `Generate all finished: ${ok} succeeded, ${fail} failed${skipped ? `, ${skipped} skipped` : ''}.`;
            if (fail && failed[0]?.error) {
                const sample = failed[0].name ? `${failed[0].name}: ${failed[0].error}` : failed[0].error;
                message += ` First error: ${sample}`;
            }
            toast(message, fail ? 'info' : 'success');
            await loadAi();
            await refreshGuide();
        } catch (err) {
            toast(err.message, 'error');
        } finally {
            if (btn && !$('ai-generate-all-status')?.textContent?.includes('running')) {
                btn.disabled = !($('ai-enabled')?.checked);
                btn.textContent = originalLabel || 'Generate All Channels';
            }
        }
    }

    async function loadEbs() {
        try {
            const settings = await api('/ebs/settings');
            if ($('ebs-display-mode')) $('ebs-display-mode').value = String(settings.ebsDisplayMode ?? 0);
            if ($('ebs-audio-mode')) $('ebs-audio-mode').value = String(settings.ebsAudioMode ?? 0);
            if ($('ebs-slate-variant')) $('ebs-slate-variant').value = String(settings.ebsSlateVariant ?? 0);
            if ($('ebs-music-source')) $('ebs-music-source').value = String(settings.ebsBackgroundMusicSource ?? 2);
            populateEbsMusicLibraries(
                settings.musicLibraries,
                settings.ebsBackgroundMusicLibraryId || '',
                settings.ebsBackgroundMusicLibraryName || 'Background Music',
                'ebs-music-library'
            );
            renderEbsCustomSlateStatus(settings.customSlates);
            updateEbsFieldVisibility();
            await loadMusicPacks();
        } catch (err) {
            reportApiError(err, 'Could not load Off Air settings.');
        }
    }

    async function saveEbsSettings() {
        const librarySelect = $('ebs-music-library');
        const selectedOption = librarySelect?.selectedOptions?.[0];
        try {
            await api('/ebs/settings', {
                method: 'PUT',
                body: JSON.stringify({
                    ebsDisplayMode: Number($('ebs-display-mode')?.value || '0'),
                    ebsAudioMode: Number($('ebs-audio-mode')?.value || '0'),
                    ebsSlateVariant: Number($('ebs-slate-variant')?.value || '0'),
                    ebsBackgroundMusicSource: Number($('ebs-music-source')?.value || '2'),
                    ebsBackgroundMusicLibraryId: selectedOption?.value || null,
                    ebsBackgroundMusicLibraryName: selectedOption?.textContent?.trim() || 'Background Music'
                })
            });
            toast('Off Air settings saved.', 'success');
            await loadEbs();
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function uploadEbsSlate(variant, inputId) {
        const input = $(inputId);
        const file = input?.files?.[0];
        if (!file) {
            toast('Choose a PNG or JPG image first.', 'error');
            return;
        }

        const formData = new FormData();
        formData.append('file', file);
        try {
            const data = await apiForm('/ebs/slates/' + variant, formData, 'POST');
            renderEbsCustomSlateStatus(data.customSlates);
            refreshEbsPreviews();
            if (input) input.value = '';
            toast('Custom Off Air slate uploaded.', 'success');
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function removeEbsSlate(variant) {
        try {
            const data = await api('/ebs/slates/' + variant, { method: 'DELETE' });
            renderEbsCustomSlateStatus(data.customSlates);
            refreshEbsPreviews();
            toast('Custom Off Air slate removed.', 'success');
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    let weatherChannelState = [];

    function renderWeatherStatus(status) {
        const el = $('weather-renderer-status');
        if (!el) return;
        const variant = status?.weatherStarVariant === 'ws3kp' ? 'ws3kp' : 'ws4kp';
        const label = variant === 'ws3kp' ? 'WeatherStar 3000' : 'WeatherStar 4000';
        const source = status?.weatherSource === 'us' ? 'United States (NOAA)'
            : status?.weatherSource === 'world' ? 'World (Open-Meteo)'
            : 'Auto (NOAA in the US, Open-Meteo worldwide)';
        el.innerHTML = `<div>${escapeHtml(label)} native live stream</div><div class="meta">${escapeHtml(source)}</div>`;
        const variantSelect = $('weather-star-variant');
        if (variantSelect) {
            variantSelect.value = variant;
        }
        if ($('weather-source')) {
            $('weather-source').value = status?.weatherSource || 'auto';
        }
        const select = $('weather-music-library');
        if (select && Array.isArray(status?.musicLibraries)) {
            const current = status.weatherMusicLibraryId || '';
            select.innerHTML = '<option value="">Local music packs (Off Air)</option>' +
                status.musicLibraries.map((lib) => `<option value="${escapeHtml(lib.id)}" ${lib.id === current ? 'selected' : ''}>${escapeHtml(lib.name)}</option>`).join('');
            if (current) select.value = current;
        }
        const zipEl = document.getElementById('weather-default-zip');
        weatherChannelState = Array.isArray(status?.weatherChannels) ? status.weatherChannels : [];
        if (zipEl) {
            const fromDefault = String(status?.weatherDefaultLocationQuery || '').trim();
            const firstChannel = weatherChannelState[0];
            const fromChannel = String(firstChannel?.location || firstChannel?.weatherLocationQuery || '').trim();
            zipEl.value = fromDefault || fromChannel;
        }
        renderWeatherZipList(weatherChannelState, zipEl ? zipEl.value : '');
        applyWeatherQueryToForm(status?.weatherStarPermalinkQuery || '');
        if ($('weather-auto-wide-169')) {
            $('weather-auto-wide-169').checked = status?.weatherStarAutoWideForSixteenNine !== false;
        }
        if ($('weather-alert-overlay-mode')) {
            const mode = status?.weatherAlertOverlayMode || 'off';
            $('weather-alert-overlay-mode').value = ['off', 'cutin', 'ticker'].includes(mode) ? mode : 'off';
        }
        if ($('weather-alert-cutin-interval')) {
            $('weather-alert-cutin-interval').value = String(status?.weatherAlertCutInIntervalMinutes || 15);
        }
        if ($('weather-alert-cutin-duration')) {
            $('weather-alert-cutin-duration').value = String(status?.weatherAlertCutInDurationSeconds || 20);
        }
        toggleWeatherAlertCutInFields();
        setWeatherAlertTestRunning(!!status?.weatherAlertTestActive);
    }

    const WEATHER_SCREEN_KEYS = [
        'hazards',
        'current-weather',
        'latest-observations',
        'hourly',
        'hourly-graph',
        'travel',
        'regional-forecast',
        'local-forecast',
        'extended-forecast',
        'almanac',
        'spc-outlook',
        'radar'
    ];

    function extractWeatherZip(value) {
        const match = String(value || '').match(/\b(\d{5})(?:-\d{4})?\b/);
        return match ? match[1] : '';
    }

    function readWeatherLocation(value, label) {
        const trimmed = String(value || '').trim();
        if (trimmed.length < 2) {
            throw new Error((label || 'Location') + ' is required (US ZIP, city, or lat,lon).');
        }
        return trimmed;
    }

    function weatherQueryFlag(params, key, fallback) {
        const raw = params.get(key);
        if (raw == null || raw === '') {
            return fallback;
        }
        return raw !== 'false' && raw !== '0';
    }

    function applyWeatherQueryToForm(query) {
        const params = new URLSearchParams(String(query || '').replace(/^\?/, ''));
        WEATHER_SCREEN_KEYS.forEach((key) => {
            const el = $('wx-' + key);
            if (el) {
                el.checked = weatherQueryFlag(params, key, true);
            }
        });
        if ($('wx-stickyKiosk')) {
            $('wx-stickyKiosk').checked = weatherQueryFlag(params, 'stickyKiosk', true);
        }
        if ($('wx-customTextEnable')) {
            $('wx-customTextEnable').checked = weatherQueryFlag(params, 'customTextEnable', false);
        }
        if ($('wx-customText')) {
            $('wx-customText').value = params.get('customText') || '';
        }
        if ($('wx-units')) {
            $('wx-units').value = params.get('units') || 'us';
        }
        if ($('wx-viewMode')) {
            $('wx-viewMode').value = params.get('viewMode') || 'standard';
        }
        if ($('wx-speed')) {
            const speed = Number(params.get('speed') || '1').toFixed(2);
            $('wx-speed').value = ['0.50', '0.75', '1.00', '1.25', '1.50'].includes(speed) ? speed : '1.00';
        }
        if ($('wx-mediaVolume')) {
            $('wx-mediaVolume').value = params.get('mediaVolume') || '0.75';
        }
    }

    function serializeWeatherQueryFromForm() {
        const params = new URLSearchParams();
        WEATHER_SCREEN_KEYS.forEach((key) => {
            params.set(key, $('wx-' + key)?.checked ? 'true' : 'false');
        });
        params.set('stickyKiosk', $('wx-stickyKiosk')?.checked ? 'true' : 'false');
        params.set('customTextEnable', $('wx-customTextEnable')?.checked ? 'true' : 'false');
        params.set('speed', $('wx-speed')?.value || '1.00');
        params.set('viewMode', $('wx-viewMode')?.value || 'standard');
        params.set('units', $('wx-units')?.value || 'us');
        params.set('customText', $('wx-customText')?.value.trim() || '');
        params.set('mediaVolume', $('wx-mediaVolume')?.value || '0.75');
        return params.toString();
    }

    function toggleWeatherAlertCutInFields() {
        const mode = $('weather-alert-overlay-mode')?.value || 'off';
        const fields = $('weather-alert-cutin-fields');
        if (fields) {
            fields.classList.toggle('hidden', mode === 'off');
        }
    }

    function setWeatherAlertTestRunning(running) {
        const stop = $('btn-stop-weather-alert-test');
        if (stop) {
            stop.disabled = !running;
        }
    }

    function armWeatherAlertTickerLoop(preview) {
        if (!preview) {
            return;
        }
        preview.querySelectorAll('.weather-alert-ticker-track').forEach((track) => {
            const text = track.querySelector('span');
            const width = text ? text.getBoundingClientRect().width : 0;
            if (!width || width < 8) {
                return;
            }
            track.style.animationDuration = Math.max(12, width / 90) + 's';
        });
    }

    function hideWeatherAlertTestPreview() {
        const preview = $('weather-alert-test-preview');
        const hint = $('weather-alert-test-hint');
        if (preview) {
            preview.innerHTML = '';
            preview.hidden = true;
            preview.classList.add('hidden');
        }
        if (hint) {
            hint.textContent = '';
            hint.hidden = true;
            hint.classList.add('hidden');
        }
    }

    function showWeatherAlertTestPreview(data) {
        const preview = $('weather-alert-test-preview');
        const hint = $('weather-alert-test-hint');
        if (!preview) {
            return;
        }
        const parts = [];
        const jpeg = data?.hazardsJpeg;
        if (data?.mode === 'cutin' && jpeg) {
            parts.push('<img alt="Sample weather alerts screen" src="data:image/jpeg;base64,' + jpeg + '">');
        } else if (data?.mode === 'cutin') {
            parts.push('<p class="hint" style="padding:0.75rem;margin:0">' + escapeHtml(data.headline || data.eventName || 'Sample weather alert') + '</p>');
        }
        if (data?.mode === 'ticker') {
            const text = escapeHtml(data.tickerText || data.headline || 'WEATHER ALERT');
            parts.push(
                '<div class="weather-alert-ticker-preview" role="img" aria-label="Sample scrolling weather alert">'
                    + '<div class="weather-alert-ticker-track">'
                    + '<span>' + text + '</span>'
                    + '<span>' + text + '</span>'
                    + '</div>'
                + '</div>'
            );
        }
        preview.innerHTML = parts.join('');
        preview.hidden = parts.length === 0;
        preview.classList.toggle('hidden', parts.length === 0);
        armWeatherAlertTickerLoop(preview);
        if (hint) {
            hint.textContent = data?.message || '';
            hint.hidden = !data?.message;
            hint.classList.toggle('hidden', !data?.message);
        }
    }

    async function testWeatherAlerts() {
        const btn = $('btn-test-weather-alerts');
        const originalLabel = btn ? btn.textContent : '';
        try {
            if (btn) {
                btn.disabled = true;
                btn.textContent = 'Testing…';
            }
            const mode = $('weather-alert-overlay-mode')?.value || 'off';
            const data = await api('/weather/alerts/test', {
                method: 'POST',
                body: JSON.stringify({
                    mode,
                    durationSeconds: Number($('weather-alert-cutin-duration')?.value || 20)
                })
            });
            showWeatherAlertTestPreview(data);
            setWeatherAlertTestRunning(true);
            toast(data.eventName ? `Sample ${data.eventName} is ready.` : 'Sample weather alert is ready.', 'success');
        } catch (err) {
            hideWeatherAlertTestPreview();
            setWeatherAlertTestRunning(false);
            toast(err.message || 'Could not test weather alerts.', 'error');
        } finally {
            if (btn) {
                btn.disabled = false;
                btn.textContent = originalLabel || 'Test alerts';
            }
        }
    }

    async function stopWeatherAlertTest() {
        const btn = $('btn-stop-weather-alert-test');
        try {
            if (btn) {
                btn.disabled = true;
            }
            const data = await api('/weather/alerts/test/stop', { method: 'POST', body: '{}' });
            hideWeatherAlertTestPreview();
            setWeatherAlertTestRunning(false);
            toast(data.message || 'Weather alert test stopped.', 'success');
        } catch (err) {
            setWeatherAlertTestRunning(true);
            toast(err.message || 'Could not stop the weather alert test.', 'error');
        }
    }

    function renderWeatherZipList(rows, defaultLocation) {
        const el = $('weather-zip-list');
        const fallback = $('weather-default-location-field');
        if (fallback) {
            fallback.classList.remove('hidden');
            fallback.hidden = false;
        }
        if (!el) {
            return;
        }
        const savedDefault = String(defaultLocation || '').trim();
        if (!rows.length) {
            el.innerHTML = '<p class="hint">New weather channels use this location. Apply a WeatherStar preset or create a Weather channel to add more.</p>';
            return;
        }
        if (rows.length === 1) {
            el.innerHTML = '<p class="hint">Used by ' + escapeHtml(rows[0].name || 'Weather') + '.</p>';
            return;
        }
        el.innerHTML = rows.map((ch) => {
            const stored = String(ch.location || ch.weatherLocationQuery || ch.zip || '').trim();
            const location = stored || savedDefault;
            return '<div class="weather-zip-row">'
                + '<div class="wx-channel-label">' + escapeHtml((ch.number || '') + ' · ' + (ch.name || 'Weather')) + '</div>'
                + '<label class="field"><span>Channel location</span>'
                + '<input class="emby-input weather-channel-zip" data-channel-id="' + escapeHtml(ch.id)
                + '" placeholder="ZIP, city, or lat,lon" value="' + escapeHtml(location) + '">'
                + '</label></div>';
        }).join('');
    }

    async function loadWeather() {
        try {
            const status = await api('/weather/status');
            renderWeatherStatus(status);
        } catch (err) {
            reportApiError(err, 'Could not load weather settings.');
        }
    }

    async function saveWeatherSettings(successMessage) {
        const musicSelect = $('weather-music-library');
        const selected = musicSelect?.selectedOptions?.[0];
        try {
            const zipEl = document.getElementById('weather-default-zip');
            const defaultRaw = (zipEl?.value || '').trim();
            let channelZips = Array.from(qa('.weather-channel-zip'))
                .filter((input) => (input.value || '').trim().length >= 2)
                .map((input) => ({
                    id: input.dataset.channelId,
                    location: readWeatherLocation(input.value, 'Location')
                }));
            const defaultLocation = defaultRaw
                ? readWeatherLocation(defaultRaw, 'Location')
                : (channelZips[0] ? channelZips[0].location : '');
            if (defaultLocation && weatherChannelState.length === 1) {
                channelZips = [{ id: weatherChannelState[0].id, location: defaultLocation }];
            } else if (defaultLocation && weatherChannelState.length && !channelZips.length) {
                channelZips = weatherChannelState.map((ch) => ({
                    id: ch.id,
                    location: defaultLocation
                }));
            }
            const saved = await api('/weather/settings', {
                method: 'PUT',
                body: JSON.stringify({
                    weatherStarPermalinkQuery: serializeWeatherQueryFromForm(),
                    weatherStarAutoWideForSixteenNine: !!$('weather-auto-wide-169')?.checked,
                    weatherStarVariant: $('weather-star-variant')?.value || 'ws4kp',
                    weatherSource: $('weather-source')?.value || 'auto',
                    weatherMusicLibraryId: musicSelect?.value || '',
                    weatherMusicLibraryName: selected && selected.value ? selected.textContent : '',
                    defaultLocation,
                    weatherAlertOverlayMode: $('weather-alert-overlay-mode')?.value || 'off',
                    weatherAlertCutInIntervalMinutes: Number($('weather-alert-cutin-interval')?.value || 15),
                    weatherAlertCutInDurationSeconds: Number($('weather-alert-cutin-duration')?.value || 20),
                    channels: channelZips
                })
            });
            toast(typeof successMessage === 'string' ? successMessage : 'Weather settings saved.', 'success');
            renderWeatherStatus(saved);
            if (zipEl && defaultLocation && !(zipEl.value || '').trim()) {
                zipEl.value = defaultLocation;
            }
            if (channelZips.length) {
                await loadChannels();
            }
        } catch (err) {
            toast(err.message || 'Could not save weather settings.', 'error');
        }
    }

    async function loadNews() {
        try {
            const [settings, feeds] = await Promise.all([
                api('/news/settings'),
                api('/news/feeds')
            ]);
            if ($('news-header')) $('news-header').value = settings.headerText || 'FlowWire News';
            if ($('news-count')) $('news-count').value = settings.articleCount || 8;
            if ($('news-refresh')) $('news-refresh').value = settings.refreshMinutes || 10;
            if ($('news-intro')) $('news-intro').value = settings.introText || '';
            if ($('news-outro')) $('news-outro').value = settings.outroText || '';
            if ($('news-tts')) $('news-tts').checked = settings.ttsEnabled !== false;
            if ($('news-tts-engine')) $('news-tts-engine').value = settings.ttsEngine === 'ai' ? 'ai' : 'google';
            if ($('news-ai-rewrite')) $('news-ai-rewrite').checked = !!settings.aiRewrite;
            if ($('news-show-header')) $('news-show-header').checked = settings.showHeader !== false;
            if ($('news-headlines-only')) $('news-headlines-only').checked = !!settings.readHeadlinesOnly;
            if ($('news-bulletin-enabled')) $('news-bulletin-enabled').checked = settings.bulletinVideosEnabled !== false;
            if ($('news-min-new')) $('news-min-new').value = settings.minNewStories || 1;
            renderNewsBulletinStatus(settings.bulletin);
            const voice = $('news-voice');
            if (voice) {
                const value = settings.voice || 'en-US';
                if (![...voice.options].some((o) => o.value === value)) {
                    const extra = document.createElement('option');
                    extra.value = value;
                    extra.textContent = value;
                    voice.appendChild(extra);
                }
                voice.value = value;
            }
            const noMusic = $('news-no-music');
            const music = $('news-music-library');
            const libraries = Array.isArray(settings.musicLibraries) ? settings.musicLibraries : [];
            if (music) {
                const current = settings.musicLibraryId || '';
                const silent = String(current).toLowerCase() === 'none';
                music.innerHTML = '<option value="">Use Off Air background music</option>' +
                    libraries.map((lib) => `<option value="${escapeHtml(lib.id)}">${escapeHtml(lib.name)}</option>`).join('');
                music.value = silent ? '' : current;
                if (!silent && music.value !== current) {
                    music.value = '';
                }
                if (noMusic) {
                    noMusic.checked = silent;
                }
                syncNewsMusicUi();
            }
            syncNewsTtsUi();
            newsFeeds = Array.isArray(feeds) ? feeds : [];
            if (newsFeeds.length === 0) {
                newsFeeds = [{ name: 'NPR News', url: 'https://feeds.npr.org/1001/rss.xml', enabled: true }];
            }
            renderNewsFeeds();
            await loadNewsPreview(false);
        } catch (err) {
            reportApiError(err, 'Could not load news settings.');
        }
    }

    function renderNewsFeeds() {
        const list = $('news-feeds-list');
        if (!list) return;
        list.innerHTML = newsFeeds.map((feed, index) => `
            <div class="news-feed-row" data-index="${index}">
                <label class="checkbox-field" title="Enabled">
                    <input type="checkbox" class="news-feed-enabled" ${feed.enabled !== false ? 'checked' : ''}>
                    <span class="channelflow-check-box" aria-hidden="true"></span>
                </label>
                <input type="text" class="emby-input news-feed-name" placeholder="Name" value="${escapeHtml(feed.name || '')}">
                <input type="url" class="emby-input news-feed-url" placeholder="https://example.com/rss.xml" value="${escapeHtml(feed.url || '')}">
                <button type="button" class="emby-button news-feed-remove" data-index="${index}">Remove</button>
            </div>
        `).join('');
        decorateCheckboxes();
        list.querySelectorAll('.news-feed-remove').forEach((btn) => {
            btn.onclick = () => {
                newsFeeds.splice(Number(btn.dataset.index), 1);
                renderNewsFeeds();
            };
        });
    }

    function collectNewsFeeds() {
        const list = $('news-feeds-list');
        if (!list) return newsFeeds;
        return Array.from(list.querySelectorAll('.news-feed-row')).map((row) => ({
            name: row.querySelector('.news-feed-name')?.value.trim() || '',
            url: row.querySelector('.news-feed-url')?.value.trim() || '',
            enabled: !!row.querySelector('.news-feed-enabled')?.checked
        })).filter((f) => f.url);
    }

    function addNewsFeedRow() {
        newsFeeds = collectNewsFeeds();
        newsFeeds.push({ name: '', url: '', enabled: true });
        renderNewsFeeds();
    }

    function syncNewsMusicUi() {
        const music = $('news-music-library');
        const noMusic = $('news-no-music');
        if (!music) {
            return;
        }
        music.disabled = !!noMusic?.checked;
    }

    function syncNewsTtsUi() {
        const enabled = !!$('news-tts')?.checked;
        const engine = $('news-tts-engine');
        if (engine) {
            engine.disabled = !enabled;
        }
    }

    async function saveNewsSettings() {
        const music = $('news-music-library');
        const noMusic = !!$('news-no-music')?.checked;
        await api('/news/settings', {
            method: 'PUT',
            body: JSON.stringify({
                headerText: $('news-header')?.value.trim() || 'FlowWire News',
                articleCount: Number($('news-count')?.value || 8),
                refreshMinutes: Number($('news-refresh')?.value || 10),
                ttsEnabled: !!$('news-tts')?.checked,
                ttsEngine: $('news-tts-engine')?.value === 'ai' ? 'ai' : 'google',
                aiRewrite: !!$('news-ai-rewrite')?.checked,
                showHeader: !!$('news-show-header')?.checked,
                readHeadlinesOnly: !!$('news-headlines-only')?.checked,
                voice: $('news-voice')?.value || 'en-US',
                introText: $('news-intro')?.value || '',
                outroText: $('news-outro')?.value || '',
                musicLibraryId: noMusic ? 'none' : (music?.value || ''),
                musicLibraryName: noMusic
                    ? 'None'
                    : (music?.value ? (music.selectedOptions?.[0]?.textContent || '').trim() : ''),
                minNewStories: Number($('news-min-new')?.value || 1),
                bulletinVideosEnabled: !!$('news-bulletin-enabled')?.checked
            })
        });
        toast('News settings saved.', 'success');
        await loadNews();
    }

    async function saveNewsFeeds() {
        newsFeeds = collectNewsFeeds();
        const saved = await api('/news/feeds', {
            method: 'PUT',
            body: JSON.stringify(newsFeeds)
        });
        newsFeeds = Array.isArray(saved) ? saved : newsFeeds;
        renderNewsFeeds();
        toast('RSS feeds saved.', 'success');
        await loadNewsPreview(true);
    }

    async function loadNewsPreview(force) {
        const data = await api('/news/preview' + (force ? '?force=true' : ''));
        const meta = $('news-preview-meta');
        const box = $('news-preview');
        if (meta) {
            meta.textContent = data.fetchedAt
                ? `Last fetched ${new Date(data.fetchedAt).toLocaleString()} · ${(data.articles || []).length} headlines`
                : 'No headlines cached yet. Save a feed and click Refresh headlines.';
        }
        if (box) {
            const articles = data.articles || [];
            box.innerHTML = articles.length
                ? articles.map((a) => `<article class="news-preview-item">${a.imageUrl ? `<img class="news-preview-thumb" src="${escapeHtml(a.imageUrl)}" alt="" referrerpolicy="no-referrer">` : ''}<div><h4>${escapeHtml(a.title)}</h4><p>${escapeHtml(a.summary || '')}</p></div></article>`).join('')
                : '<p class="hint">No headlines. Add an enabled RSS URL and refresh.</p>';
        }
    }

    function formatNewsBulletinStatus(bulletin) {
        if (!bulletin) {
            return 'No bulletin has run yet.';
        }
        if (bulletin.isRunning) {
            return 'Creating news video… intro, speech, pictures, and outro. The current video stays until this one finishes.';
        }
        const leftover = formatNewsLeftovers(bulletin.leftovers);
        const next = bulletin.nextRunAt ? `Next run ${new Date(bulletin.nextRunAt).toLocaleString()}.` : '';
        if (!bulletin.lastRunAt) {
            return `${next || 'No bulletin has run yet.'}${leftover}`.trim();
        }
        const when = new Date(bulletin.lastRunAt).toLocaleString();
        if (bulletin.lastCreated) {
            const path = bulletin.lastVideoPath ? ` Saved ${bulletin.lastVideoPath}.` : '';
            return `Last video ${when} · ${bulletin.lastEncodedStoryCount || 0} stories.${path} ${next}${leftover}`.trim();
        }
        const reason = bulletin.lastSkipReason || 'skipped';
        return `Last run ${when}: ${reason} ${next}${leftover}`.trim();
    }

    function formatNewsLeftovers(leftovers) {
        if (!leftovers) {
            return '';
        }
        const bits = [];
        if (leftovers.workFolders) bits.push(`${leftovers.workFolders} failed job folder${leftovers.workFolders === 1 ? '' : 's'}`);
        if (leftovers.partialFiles) bits.push(`${leftovers.partialFiles} incomplete file${leftovers.partialFiles === 1 ? '' : 's'}`);
        if (leftovers.extraVideos) bits.push(`${leftovers.extraVideos} old video${leftovers.extraVideos === 1 ? '' : 's'}`);
        if (leftovers.scratchFiles) bits.push(`${leftovers.scratchFiles} leftover scratch item${leftovers.scratchFiles === 1 ? '' : 's'}`);
        return bits.length ? ` Leftovers: ${bits.join(', ')}.` : '';
    }

    function setNewsBulletinButtons(running) {
        ['btn-run-news-bulletin', 'btn-run-news-bulletin-task'].forEach((id) => {
            const btn = $(id);
            if (!btn) {
                return;
            }
            btn.disabled = !!running;
            if (btn.id === 'btn-run-news-bulletin-task') {
                btn.textContent = running ? 'Creating…' : 'Create News Video';
            } else {
                btn.textContent = running ? 'Creating…' : 'Create news video now';
            }
        });
    }

    function renderNewsBulletinStatus(bulletin) {
        const text = formatNewsBulletinStatus(bulletin);
        const newsEl = $('news-bulletin-status');
        const taskEl = $('task-news-bulletin-status');
        if (newsEl) newsEl.textContent = text;
        if (taskEl) taskEl.textContent = text;
        setNewsBulletinButtons(!!bulletin?.isRunning);
    }

    async function runNewsBulletin() {
        setNewsBulletinButtons(true);
        try {
            const start = await api('/news/bulletins/run', { method: 'POST' });
            toast(start?.alreadyRunning
                ? 'News video is already being created…'
                : 'Creating news video in the background…', 'info');
            renderNewsBulletinStatus({ ...(start?.bulletin || {}), isRunning: true });
            const bulletin = await pollNewsBulletinUntilIdle();
            if (bulletin?.lastCreated) {
                toast('News video created.', 'success');
            } else {
                toast(bulletin?.lastSkipReason || 'News video finished.', 'success');
            }
        } finally {
            setNewsBulletinButtons(false);
        }
    }

    async function pollNewsBulletinUntilIdle() {
        const deadline = Date.now() + 15 * 60 * 1000;
        while (Date.now() < deadline) {
            const settings = await api('/news/settings');
            renderNewsBulletinStatus(settings.bulletin);
            if (!settings.bulletin?.isRunning) {
                return settings.bulletin;
            }
            await new Promise((resolve) => setTimeout(resolve, 3000));
        }
        throw new Error('News video is still encoding. Leave this tab open or check status again in a few minutes.');
    }

    async function cleanupNewsBulletins() {
        const result = await api('/news/bulletins/cleanup', { method: 'POST' });
        renderNewsBulletinStatus(result?.bulletin);
        const removed = (result?.removedWorkFolders || 0)
            + (result?.removedPartialFiles || 0)
            + (result?.removedOldVideos || 0)
            + (result?.removedScratchFiles || 0);
        toast(removed
            ? `Removed ${removed} leftover news file${removed === 1 ? '' : 's'}. The current video was kept.`
            : 'No leftover news jobs or old videos to remove.', 'success');
    }

    let catalogCleanupPollTimer = null;
    let catalogCleanupPendingRun = null;

    function beginCatalogCleanupPendingRun(status) {
        // The run endpoint queues work in the background, so its response can be
        // built before the server flips the running flag. Remember what "not yet
        // started" looks like so an early poll cannot stop the status polling.
        catalogCleanupPendingRun = {
            since: Date.now(),
            baselineCleanupCompletedAt: (status && status.lastCompletedAt) || null,
            baselineScanCompletedAt: (status && status.localScan && status.localScan.lastCompletedAt) || null,
            sawRun: false
        };
    }

    function shouldContinueCatalogCleanupPolling(status) {
        const running = !!(status.isRunning || (status.localScan && status.localScan.isRunning));
        const pending = catalogCleanupPendingRun;
        if (running) {
            if (pending) {
                pending.sawRun = true;
            }
            return true;
        }

        if (!pending) {
            return false;
        }

        const cleanupFinished = !!(status.lastCompletedAt
            && status.lastCompletedAt !== pending.baselineCleanupCompletedAt);
        const scanFinished = !!(status.localScan && status.localScan.lastCompletedAt
            && status.localScan.lastCompletedAt !== pending.baselineScanCompletedAt);
        if (pending.sawRun || cleanupFinished || scanFinished || Date.now() - pending.since > 60000) {
            // The queued run finished (this status already shows its result),
            // or it never started within a minute — stop watching either way.
            catalogCleanupPendingRun = null;
            return false;
        }

        // Queued on the server but the running flag has not flipped yet.
        return true;
    }

    function renderCatalogCleanupStatus(status) {
        const el = $('catalog-cleanup-status');
        const runBtn = $('btn-run-catalog-cleanup');
        const grace = $('catalog-cleanup-grace');
        if (!el || !status) {
            return;
        }

        if (grace && typeof status.gracePeriodDays === 'number') {
            grace.value = String(status.gracePeriodDays);
            const libraryGrace = $('library-cleanup-grace');
            if (libraryGrace) {
                libraryGrace.value = String(status.gracePeriodDays);
            }
        }

        const libraryStatus = $('library-cleanup-status');
        const writeStatus = (text) => {
            el.textContent = text;
            if (libraryStatus) {
                libraryStatus.textContent = text;
            }
        };

        if (status.isRunning) {
            writeStatus('Catalog cleanup is running… marking missing items, scanning remapped local files, then deleting rows past the grace period.');
            if (runBtn) {
                runBtn.disabled = true;
                runBtn.textContent = 'Cleaning…';
            }
            renderCatalogLocalScanStatus(status);
            return;
        }

        if (runBtn) {
            runBtn.disabled = !!status.localScan?.isRunning;
            runBtn.textContent = 'Run Catalog Cleanup';
        }

        if (status.lastError) {
            writeStatus(
                `Last run failed: ${status.lastError}` +
                (status.lastCompletedAt ? ` · previous success ${new Date(status.lastCompletedAt).toLocaleString()}` : '')
            );
        } else if (status.lastCompletedAt) {
            writeStatus(
                `Last run ${new Date(status.lastCompletedAt).toLocaleString()}: marked ${status.markedMissing} missing, removed ${status.removed}. ` +
                `${status.currentlyMissing} catalog row(s) currently missing (waiting on the ${status.gracePeriodDays}-day grace period).`
            );
        } else {
            writeStatus(
                `${status.currentlyMissing} catalog row(s) currently missing. Grace period is ${status.gracePeriodDays} day(s). Run cleanup after a catalog sync, or wait for the daily task.`
            );
        }

        renderCatalogLocalScanStatus(status);
    }

    function renderCatalogLocalScanStatus(status) {
        const el = $('catalog-local-scan-status');
        const scanBtn = $('btn-scan-local-catalog');
        const scan = status?.localScan;
        if (!el) {
            return;
        }

        const writeScan = (text) => {
            el.textContent = text;
            const libraryScan = $('library-scan-status');
            if (libraryScan) {
                libraryScan.textContent = text;
            }
        };

        if (scan?.isRunning) {
            writeScan(
                `Scanning remapped local files… ${scan.processedItems}/${scan.totalItems} checked · ${scan.found} found · ${scan.restored} restored · ${scan.markedMissing} marked missing.`
            );
            if (scanBtn) {
                scanBtn.disabled = true;
                scanBtn.textContent = 'Scanning…';
            }
            return;
        }

        if (scanBtn) {
            scanBtn.disabled = !!status?.isRunning;
            scanBtn.textContent = 'Scan Local Files';
        }

        if (scan?.lastError) {
            writeScan(
                `Last scan failed: ${scan.lastError}` +
                (scan.lastCompletedAt ? ` · previous success ${new Date(scan.lastCompletedAt).toLocaleString()}` : '')
            );
            return;
        }

        if (scan?.lastCompletedAt) {
            writeScan(
                `Last scan ${new Date(scan.lastCompletedAt).toLocaleString()}: ${scan.found} file(s) present at remapped paths, restored ${scan.restored}, marked ${scan.markedMissing} missing, skipped ${scan.skipped} with no path.`
            );
            return;
        }

        writeScan('Scan catalog items against remapped local files. If the remapped path exists, the item is present.');
    }

    async function loadCatalogCleanup() {
        if (!syncConfigPage()) {
            return;
        }

        try {
            const status = await api('/tasks/catalog-cleanup');
            renderCatalogCleanupStatus(status);
            if (shouldContinueCatalogCleanupPolling(status)) {
                startCatalogCleanupPolling();
            } else {
                stopCatalogCleanupPolling();
            }
        } catch (err) {
            const el = $('catalog-cleanup-status');
            if (el) {
                el.textContent = 'Could not load catalog cleanup status.';
            }
        }
    }

    function startCatalogCleanupPolling() {
        if (catalogCleanupPollTimer) {
            return;
        }

        catalogCleanupPollTimer = setInterval(() => {
            loadCatalogCleanup().catch(() => {});
        }, 3000);
    }

    function stopCatalogCleanupPolling() {
        if (catalogCleanupPollTimer) {
            clearInterval(catalogCleanupPollTimer);
            catalogCleanupPollTimer = null;
        }
    }

    async function saveCatalogCleanupSettings() {
        const days = Number($('catalog-cleanup-grace')?.value || '7');
        const status = await api('/tasks/catalog-cleanup', {
            method: 'PUT',
            body: JSON.stringify({ gracePeriodDays: days })
        });
        renderCatalogCleanupStatus(status);
        toast('Catalog cleanup grace period saved.', 'success');
    }

    async function forceWatchedChannelsToCommercial() {
        if (!confirm('Cut every currently watched channel to commercial in 15 seconds?')) {
            return;
        }

        const statusEl = $('force-commercial-status');
        const button = $('btn-force-commercial');
        if (button) {
            button.disabled = true;
        }
        if (statusEl) {
            statusEl.textContent = 'Forcing watched channels to commercial…';
        }

        try {
            const result = await api('/tasks/force-commercial', { method: 'POST' });
            const lines = [result.message || 'Done.'];
            (result.forced || []).forEach((item) => {
                lines.push(`${item.channelName}: ${item.message}`);
            });
            (result.skipped || []).forEach((item) => {
                lines.push(`${item.channelName}: ${item.message}`);
            });
            if (statusEl) {
                statusEl.textContent = lines.join('\n');
            }
            if (result.forcedCount > 0) {
                toast(result.message, 'success');
            } else {
                toast(result.message || 'No channels were cut to commercial.', 'info');
            }
        } finally {
            if (button) {
                button.disabled = false;
            }
        }
    }

    async function runCatalogCleanup() {
        const result = await api('/tasks/catalog-cleanup/run', { method: 'POST' });
        if (result.alreadyRunning) {
            toast('Catalog cleanup is already running.', 'info');
        } else {
            toast('Catalog cleanup started.', 'success');
        }
        if (result.status) {
            renderCatalogCleanupStatus(result.status);
        }
        beginCatalogCleanupPendingRun(result.status);
        startCatalogCleanupPolling();
        beginLibraryTaskPopup('cleanup', 'Catalog cleanup', mapCleanupTask(result.status));
        await loadCatalogCleanup();
    }

    async function runCatalogLocalScan() {
        const result = await api('/tasks/catalog-cleanup/scan-local', { method: 'POST' });
        if (result.alreadyRunning) {
            toast('A catalog scan or cleanup is already running.', 'info');
        } else {
            toast('Local file scan started.', 'success');
        }
        if (result.status) {
            renderCatalogCleanupStatus(result.status);
        }
        beginCatalogCleanupPendingRun(result.status);
        startCatalogCleanupPolling();
        beginLibraryTaskPopup('cleanup', 'Catalog cleanup', mapCleanupTask(result.status) || {
            isRunning: true,
            message: 'Scanning remapped local files…'
        });
        await loadCatalogCleanup();
    }

    let libraryScanPollTimer = null;

    function renderLibraryScanStatus(status) {
        const el = $('library-connection-scan-status');
        const runBtn = $('btn-run-library-scan');
        if (!el || !status) {
            return;
        }

        if (runBtn) {
            runBtn.disabled = !!status.isRunning;
            runBtn.textContent = status.isRunning ? 'Scanning…' : 'Scan Libraries Now';
        }

        const next = status.nextRunAt ? new Date(status.nextRunAt).toLocaleString() : 'the next 6-hour mark';
        if (status.isRunning) {
            el.textContent = 'Library scan is running… syncing each enabled connection for new files.';
            return;
        }

        if (status.lastError) {
            el.textContent =
                `Last run failed: ${status.lastError}` +
                (status.lastCompletedAt ? ` · finished ${new Date(status.lastCompletedAt).toLocaleString()}` : '') +
                ` · next at ${next}.`;
            return;
        }

        if (status.lastCompletedAt) {
            el.textContent =
                `Last run ${new Date(status.lastCompletedAt).toLocaleString()}: synced ${status.lastSynced} connection(s), ` +
                `imported ${status.lastImported} item(s), skipped ${status.lastSkipped}. Next at ${next}.`;
            return;
        }

        el.textContent = `Next library scan at ${next}.`;
    }

    async function loadLibraryScan() {
        if (!syncConfigPage()) {
            return;
        }

        try {
            const status = await api('/tasks/library-scan');
            renderLibraryScanStatus(status);
            if (status.isRunning) {
                startLibraryScanPolling();
            } else {
                stopLibraryScanPolling();
            }
        } catch (err) {
            const el = $('library-connection-scan-status');
            if (el) {
                el.textContent = 'Could not load library scan status.';
            }
        }
    }

    function startLibraryScanPolling() {
        if (libraryScanPollTimer) {
            return;
        }

        libraryScanPollTimer = setInterval(() => {
            loadLibraryScan().catch(() => {});
        }, 3000);
    }

    function stopLibraryScanPolling() {
        if (libraryScanPollTimer) {
            clearInterval(libraryScanPollTimer);
            libraryScanPollTimer = null;
        }
    }

    async function runLibraryScan() {
        const result = await api('/tasks/library-scan', { method: 'POST' });
        if (result.alreadyRunning) {
            toast('A library scan is already running.', 'info');
        } else {
            toast('Library scan started.', 'success');
        }
        if (result.status) {
            renderLibraryScanStatus(result.status);
        }
        startLibraryScanPolling();
        catalogSyncKind = libraryPage && libraryPage !== 'connections' && libraryPage !== 'removed' ? libraryPage : catalogSyncKind;
        catalogSyncClosed = false;
        catalogSyncSeenRunning = true;
        catalogSyncExpectingStart = Date.now();
        renderCatalogSyncPopup({
            running: true,
            phase: 'starting',
            kicker: 'Library scan',
            serverName: 'Library scan',
            message: 'Starting library scan…',
            percent: null,
            items: 0,
            saved: 0,
            total: 0
        });
        startCatalogSyncPolling();
        await loadLibraryScan();
    }

    let ffprobeScanPollTimer = null;

    function renderFfprobeScanStatus(status) {
        const el = $('ffprobe-scan-status');
        const runBtn = $('btn-run-ffprobe-scan');
        if (!el || !status) {
            return;
        }

        if (runBtn) {
            runBtn.disabled = !!status.isRunning;
            runBtn.textContent = status.isRunning ? 'Scanning…' : 'Scan Missing Chapters';
        }

        const next = status.nextRunAt ? new Date(status.nextRunAt).toLocaleString() : 'midnight';
        if (status.isRunning) {
            el.textContent = status.message || 'ffprobe is scanning videos that still have no chapter data…';
            return;
        }

        if (status.lastError) {
            el.textContent =
                `Last run failed: ${status.lastError}` +
                (status.lastCompletedAt ? ` · finished ${new Date(status.lastCompletedAt).toLocaleString()}` : '') +
                ` · next at ${next}.`;
            return;
        }

        if (status.lastCompletedAt) {
            el.textContent =
                `Last run ${new Date(status.lastCompletedAt).toLocaleString()}: probed ${status.lastProbed} video(s), ` +
                `found chapters on ${status.lastWithChapters}, skipped ${status.lastSkipped}, failed ${status.lastFailed}. Next at ${next}.`;
            return;
        }

        el.textContent = `Next ffprobe chapter scan at ${next}.`;
    }

    async function loadFfprobeScan() {
        if (!syncConfigPage()) {
            return;
        }

        try {
            const status = await api('/tasks/ffprobe');
            renderFfprobeScanStatus(status);
            if (status.isRunning) {
                startFfprobeScanPolling();
            } else {
                stopFfprobeScanPolling();
            }
        } catch (err) {
            const el = $('ffprobe-scan-status');
            if (el) {
                el.textContent = 'Could not load ffprobe chapter scan status.';
            }
        }
    }

    function startFfprobeScanPolling() {
        if (ffprobeScanPollTimer) {
            return;
        }

        ffprobeScanPollTimer = setInterval(() => {
            loadFfprobeScan().catch(() => {});
        }, 3000);
    }

    function stopFfprobeScanPolling() {
        if (ffprobeScanPollTimer) {
            clearInterval(ffprobeScanPollTimer);
            ffprobeScanPollTimer = null;
        }
    }

    async function runFfprobeScan() {
        const result = await api('/tasks/ffprobe', { method: 'POST' });
        if (result.alreadyRunning) {
            toast('An ffprobe chapter scan is already running.', 'info');
        } else {
            toast('ffprobe chapter scan started.', 'success');
        }
        if (result.status) {
            renderFfprobeScanStatus(result.status);
        }
        startFfprobeScanPolling();
        beginLibraryTaskPopup('ffprobe', 'ffprobe chapters', result.status);
        await loadFfprobeScan();
    }

    let trueAspectScanPollTimer = null;

    function renderTrueAspectScanStatus(status) {
        const el = $('true-aspect-scan-status');
        const runBtn = $('btn-run-true-aspect-scan');
        if (!el || !status) {
            return;
        }

        if (runBtn) {
            runBtn.disabled = !!status.isRunning;
            runBtn.textContent = status.isRunning ? 'Scanning…' : 'Scan Missing True Aspect';
        }

        const next = status.nextRunAt ? new Date(status.nextRunAt).toLocaleString() : 'midnight';
        if (status.isRunning) {
            el.textContent = status.message || 'ffmpeg is sampling videos that still have no TrueAspectRatio…';
            return;
        }

        if (status.lastError) {
            el.textContent =
                `Last run failed: ${status.lastError}` +
                (status.lastCompletedAt ? ` · finished ${new Date(status.lastCompletedAt).toLocaleString()}` : '') +
                ` · next at ${next}.`;
            return;
        }

        if (status.lastCompletedAt) {
            el.textContent =
                `Last run ${new Date(status.lastCompletedAt).toLocaleString()}: probed ${status.lastProbed} video(s), ` +
                `measured ${status.lastMeasured}, skipped ${status.lastSkipped}, failed ${status.lastFailed}. Next at ${next}.`;
            return;
        }

        el.textContent = `Next true-aspect scan at ${next}.`;
    }

    async function loadTrueAspectScan() {
        if (!syncConfigPage()) {
            return;
        }

        try {
            const status = await api('/tasks/true-aspect');
            renderTrueAspectScanStatus(status);
            if (status.isRunning) {
                startTrueAspectScanPolling();
            } else {
                stopTrueAspectScanPolling();
            }
        } catch (err) {
            const el = $('true-aspect-scan-status');
            if (el) {
                el.textContent = 'Could not load true-aspect scan status.';
            }
        }
    }

    function startTrueAspectScanPolling() {
        if (trueAspectScanPollTimer) {
            return;
        }

        trueAspectScanPollTimer = setInterval(() => {
            loadTrueAspectScan().catch(() => {});
        }, 3000);
    }

    function stopTrueAspectScanPolling() {
        if (trueAspectScanPollTimer) {
            clearInterval(trueAspectScanPollTimer);
            trueAspectScanPollTimer = null;
        }
    }

    async function runTrueAspectScan() {
        const result = await api('/tasks/true-aspect', { method: 'POST' });
        if (result.alreadyRunning) {
            toast('A true-aspect scan is already running.', 'info');
        } else {
            toast('True-aspect scan started.', 'success');
        }
        if (result.status) {
            renderTrueAspectScanStatus(result.status);
        }
        startTrueAspectScanPolling();
        beginLibraryTaskPopup('true-aspect', 'True aspect ratio', result.status);
        await loadTrueAspectScan();
    }

    let commercialBreakScanPollTimer = null;
    let commercialBreakSettings = null;

    function commercialBreakNumber(value, fallback) {
        const n = Number(value);
        return Number.isFinite(n) ? n : fallback;
    }

    function commercialBreakFieldValue(id) {
        const el = $(id);
        return el && el.value !== '' ? el.value : null;
    }

    function commercialBreakSettingsFromForm() {
        const current = commercialBreakSettings || {};
        const minGapEl = $('cb-min-gap-min');
        return {
            scanEnabled: !!($('commercial-breaks-scan-enabled') && $('commercial-breaks-scan-enabled').checked),
            writeChaptersToFiles: !!($('commercial-breaks-write-files') && $('commercial-breaks-write-files').checked),
            silenceDb: commercialBreakNumber(commercialBreakFieldValue('cb-silence-db') ?? current.silenceDb, -40),
            silenceMinSeconds: commercialBreakNumber(commercialBreakFieldValue('cb-silence-min') ?? current.silenceMinSeconds, 0.3),
            blackPixThreshold: commercialBreakNumber(commercialBreakFieldValue('cb-black-pix') ?? current.blackPixThreshold, 0.10),
            blackPictureRatio: commercialBreakNumber(commercialBreakFieldValue('cb-black-ratio') ?? current.blackPictureRatio, 0.95),
            blackMinFrames: commercialBreakNumber(commercialBreakFieldValue('cb-black-frames') ?? current.blackMinFrames, 6),
            confidencePercent: commercialBreakNumber(commercialBreakFieldValue('cb-confidence') ?? current.confidencePercent, 70),
            minGapSeconds: minGapEl
                ? commercialBreakNumber(minGapEl.value, 8) * 60
                : commercialBreakNumber(current.minGapSeconds, 480)
        };
    }

    function applyCommercialBreakSwitches(settings) {
        commercialBreakSettings = settings || commercialBreakSettings;
        const scan = $('commercial-breaks-scan-enabled');
        const write = $('commercial-breaks-write-files');
        if (scan && commercialBreakSettings) {
            scan.checked = !!commercialBreakSettings.scanEnabled;
        }
        if (write && commercialBreakSettings) {
            write.checked = !!commercialBreakSettings.writeChaptersToFiles;
        }
        decorateCheckboxes();
    }

    function renderCommercialBreakScanStatus(status) {
        const el = $('commercial-breaks-scan-status');
        const runBtn = $('btn-run-commercial-breaks-scan');
        const bar = $('commercial-breaks-scan-bar');
        const fill = $('commercial-breaks-scan-bar-fill');
        if (!el || !status) {
            return;
        }

        if (runBtn) {
            const enabled = !!(status.scanEnabled || (commercialBreakSettings && commercialBreakSettings.scanEnabled));
            runBtn.disabled = !!status.isRunning || !enabled;
            runBtn.textContent = status.isRunning ? 'Scanning…' : 'Run now';
        }

        if (bar && fill) {
            const running = !!status.isRunning;
            bar.classList.toggle('hidden', !running);
            bar.hidden = !running;
            const indeterminate = running && (status.percent == null || status.percent === undefined);
            bar.dataset.indeterminate = indeterminate ? 'true' : 'false';
            fill.style.width = indeterminate ? '' : Math.max(0, Math.min(100, Number(status.percent) || 0)) + '%';
        }

        const next = status.nextRunAt ? new Date(status.nextRunAt).toLocaleString() : 'midnight';
        if (status.isRunning) {
            el.textContent = status.message || 'ffmpeg is scanning videos for commercial-break spots…';
            return;
        }

        if (status.lastError) {
            el.textContent =
                `Last run failed: ${status.lastError}` +
                (status.lastCompletedAt ? ` · finished ${new Date(status.lastCompletedAt).toLocaleString()}` : '') +
                ` · next ${next}`;
            return;
        }

        if (status.lastCompletedAt) {
            el.textContent = `${status.message || 'Finished.'} Next midnight scan at ${next}.`;
            return;
        }

        el.textContent = status.message || `Next commercial-break scan at ${next}.`;
    }

    async function loadCommercialBreakScan() {
        if (!commercialBreakSettings) {
            try {
                applyCommercialBreakSwitches(await api('/tasks/commercial-breaks/settings'));
            } catch (ignore) {
                // Status can still load if settings are unavailable.
            }
        }

        try {
            const status = await api('/tasks/commercial-breaks');
            renderCommercialBreakScanStatus(status);
            if (status && status.isRunning) {
                startCommercialBreakScanPolling();
            } else {
                stopCommercialBreakScanPolling();
            }
        } catch (ignore) {
            const el = $('commercial-breaks-scan-status');
            if (el) {
                el.textContent = 'Could not load commercial-break scan status.';
            }
        }
    }

    function startCommercialBreakScanPolling() {
        if (commercialBreakScanPollTimer) {
            return;
        }

        commercialBreakScanPollTimer = setInterval(() => {
            loadCommercialBreakScan().catch(() => {});
        }, 3000);
    }

    function stopCommercialBreakScanPolling() {
        if (commercialBreakScanPollTimer) {
            clearInterval(commercialBreakScanPollTimer);
            commercialBreakScanPollTimer = null;
        }
    }

    async function saveCommercialBreakSettings(partial) {
        if (!commercialBreakSettings) {
            try {
                commercialBreakSettings = await api('/tasks/commercial-breaks/settings');
            } catch (ignore) {
                commercialBreakSettings = {};
            }
        }
        const next = Object.assign({}, commercialBreakSettings || {}, commercialBreakSettingsFromForm(), partial || {});
        const saved = await api('/tasks/commercial-breaks/settings', { method: 'PUT', body: JSON.stringify(next) });
        commercialBreakSettings = saved;
        applyCommercialBreakSwitches(saved);
        await loadCommercialBreakScan();
        return saved;
    }

    async function runCommercialBreakScan() {
        const result = await api('/tasks/commercial-breaks', { method: 'POST' });
        if (result.disabled) {
            toast('Turn on Scan commercial breaks first.', 'info');
            if (result.status) {
                renderCommercialBreakScanStatus(result.status);
            }
            return;
        }
        if (result.alreadyRunning) {
            toast('A commercial-break scan is already running.', 'info');
        } else {
            toast('Commercial-break scan started.', 'success');
        }
        if (result.status) {
            renderCommercialBreakScanStatus(result.status);
        }
        startCommercialBreakScanPolling();
        beginLibraryTaskPopup('commercial-breaks', 'Commercial breaks', result.status);
        await loadCommercialBreakScan();
    }

    function openCommercialBreakSettings() {
        const s = commercialBreakSettings || {};
        openModal('Commercial break settings', `
            <p class="hint">Tune blackdetect and silencedetect. Isolated black or silence scores about 45% and is dropped at the default 70% confidence. Overlapping black+silence scores about 90–100%. One break about every 10 minutes (file length); at most one in each 10-minute window. If that count is short, a second pass uses black frames only (no audio).</p>
            <label class="field">
                <span>How quiet (dB)</span>
                <input id="cb-silence-db" type="number" min="-90" max="0" step="1" value="${escapeHtml(String(s.silenceDb ?? -40))}" class="emby-input">
            </label>
            <p class="hint">Noise floor for silencedetect. Typical: −40.</p>
            <label class="field">
                <span>How long silence must last (seconds)</span>
                <input id="cb-silence-min" type="number" min="0.05" max="5" step="0.05" value="${escapeHtml(String(s.silenceMinSeconds ?? 0.3))}" class="emby-input">
            </label>
            <label class="field">
                <span>Darkness (pix_th)</span>
                <input id="cb-black-pix" type="number" min="0.01" max="0.5" step="0.01" value="${escapeHtml(String(s.blackPixThreshold ?? 0.10))}" class="emby-input">
            </label>
            <p class="hint">How dark a pixel must be (0–1).</p>
            <label class="field">
                <span>Picture ratio (pic_th)</span>
                <input id="cb-black-ratio" type="number" min="0.5" max="1" step="0.01" value="${escapeHtml(String(s.blackPictureRatio ?? 0.95))}" class="emby-input">
            </label>
            <p class="hint">Fraction of the frame that must be that dark.</p>
            <label class="field">
                <span>Min black frames</span>
                <input id="cb-black-frames" type="number" min="1" max="120" step="1" value="${escapeHtml(String(s.blackMinFrames ?? 6))}" class="emby-input">
            </label>
            <label class="field">
                <span>Confidence threshold (%)</span>
                <input id="cb-confidence" type="number" min="1" max="100" step="1" value="${escapeHtml(String(s.confidencePercent ?? 70))}" class="emby-input">
            </label>
            <label class="field">
                <span>Min gap between breaks (minutes)</span>
                <input id="cb-min-gap-min" type="number" min="1" max="30" step="0.5" value="${escapeHtml(String(((s.minGapSeconds ?? 480) / 60).toFixed(1)))}" class="emby-input">
            </label>
            <p class="hint">Stops two accepted spots from landing too close, including across a 10-minute window edge.</p>
        `, `
            <button type="button" class="emby-button" id="cb-settings-reset">Reset to stock</button>
            <button type="button" class="emby-button" id="cb-settings-cancel">Cancel</button>
            <button type="button" class="raised button-submit emby-button" id="cb-settings-save">Save</button>
        `);
        const cancel = $('cb-settings-cancel');
        const save = $('cb-settings-save');
        const reset = $('cb-settings-reset');
        if (cancel) {
            cancel.addEventListener('click', closeModal);
        }
        if (reset) {
            reset.addEventListener('click', () => applyStockCommercialBreakFields());
        }
        if (save) {
            save.addEventListener('click', () => {
                saveCommercialBreakSettings().then(() => {
                    closeModal();
                    toast('Commercial-break settings saved.', 'success');
                }).catch((e) => toast(e.message, 'error'));
            });
        }
    }

    function applyStockCommercialBreakFields() {
        const set = (id, value) => {
            const el = $(id);
            if (el) {
                el.value = String(value);
            }
        };
        set('cb-silence-db', -40);
        set('cb-silence-min', 0.3);
        set('cb-black-pix', 0.10);
        set('cb-black-ratio', 0.95);
        set('cb-black-frames', 6);
        set('cb-confidence', 70);
        set('cb-min-gap-min', '8.0');
    }

    function renderAboutDl(elementId, rows) {
        const el = $(elementId);
        if (!el) {
            return;
        }
        el.innerHTML = rows
            .filter((row) => row[1] != null && row[1] !== '')
            .map(([label, value, href]) => {
                const text = escapeHtml(String(value));
                const dd = href
                    ? `<a href="${escapeHtml(String(href))}" target="_blank" rel="noopener">${text}</a>`
                    : text;
                return `<div class="about-row"><dt>${escapeHtml(label)}</dt><dd>${dd}</dd></div>`;
            })
            .join('') || '<div class="about-row"><dt>Status</dt><dd>Unavailable</dd></div>';
    }

    async function loadAbout() {
        try {
            const info = await api('/about');
            const app = info.app || {};
            const system = info.system || {};
            const transcode = info.transcode || {};
            renderAboutDl('about-app', [
                ['Author', app.author, app.authorUrl],
                ['Version', app.version],
                ['Build', app.revision],
                ['Packaging', app.packagingLabel || (app.packaging === 'docker' ? 'Docker' : 'Non-Docker')],
                ['Image', app.image],
                ['Runtime', app.framework],
                ['Homepage', app.homepage, app.homepage]
            ]);
            renderAboutDl('about-system', [
                ['Operating system', system.os],
                ['Architecture', system.architecture],
                ['Host', system.machineName],
                ['Environment', system.environmentName],
                ['CPU cores', system.processorCount],
                ['Memory (working set)', system.workingSet],
                ['GC heap', system.gcHeap],
                ['Uptime', system.uptime],
                ['Time zone', system.timeZone],
                ['App time', system.appTime],
                ['UTC', system.utcTime],
                ['Listen port', system.listenPort],
                ['Config folder', system.configFolder],
                ['PostgreSQL host', system.postgresHost],
                ['Active viewers', system.activeViewers]
            ]);
            const vaapi = transcode.useVaapi || transcode.useQsv
                ? `Available (${transcode.vaapiDevice})`
                : (transcode.vaapiDeviceExists ? transcode.vaapiDevice : `Missing (${transcode.vaapiDevice || 'none'})`);
            const pipeline = info.pipeline || transcode.pipeline || {};
            const normalization = info.normalization || pipeline.target || {};
            renderAboutDl('about-stream', [
                ['Pipeline', pipeline.summary],
                ['Encoder', pipeline.encoder || transcode.encoder],
                ['Hardware acceleration', pipeline.acceleration || transcode.hardwareAcceleration],
                ['Source', transcode.source === 'saved' ? 'Saved on Transcode' : 'Container / environment default'],
                ['GPU device', vaapi],
                ['GPU', transcode.gpuSummary],
                ['Target', normalization.summary],
                ['Resolution', normalization.resolution],
                ['Frame rate', normalization.frameRate],
                ['Video', normalization.videoCodec],
                ['Profile', normalization.videoProfile],
                ['Audio', `${normalization.audioCodec || ''} ${normalization.audioChannels || ''}`.trim()],
                ['FFmpeg path', transcode.ffmpegPath],
                ['FFmpeg', transcode.ffmpegVersion]
            ]);
        } catch (err) {
            reportApiError(err, 'Could not load About information.');
            renderAboutDl('about-app', [['Status', err.message || 'Could not load About information.']]);
        }
    }

    let gpuCapabilities = null;

    function replaceSelectOptions(selectId, options, selected) {
        const select = $(selectId);
        if (!select || !Array.isArray(options) || options.length === 0) {
            return selected;
        }

        const wanted = selected == null || selected === undefined ? select.value : String(selected);
        select.innerHTML = '';
        options.forEach((item) => {
            const opt = document.createElement('option');
            opt.value = item.value;
            opt.textContent = item.label || item.value;
            select.appendChild(opt);
        });
        if ([...select.options].some((option) => option.value === wanted)) {
            select.value = wanted;
            return wanted;
        }

        if (wanted) {
            const extra = document.createElement('option');
            extra.value = wanted;
            extra.textContent = wanted;
            select.appendChild(extra);
            select.value = wanted;
            return wanted;
        }

        select.value = select.options[0].value;
        return select.value;
    }

    function selectedAccelOption() {
        const accel = $('transcode-hwaccel')?.value || 'none';
        const list = gpuCapabilities?.accelerations || [];
        return list.find((item) => item.value === accel) || list[0] || null;
    }

    function applyNormalizationLimits(limits, settings) {
        if (!limits) {
            return;
        }

        replaceSelectOptions('norm-resolution', limits.resolutions, settings?.resolution);
        replaceSelectOptions('norm-framerate', limits.frameRates, settings?.frameRate);
        replaceSelectOptions('norm-video-codec', limits.videoCodecs, settings?.videoCodec);
        replaceSelectOptions('norm-video-profile', limits.h264Profiles, settings?.videoProfile);
    }

    function syncTranscodeUi() {
        const accel = $('transcode-hwaccel')?.value || 'none';
        const vaapiField = $('transcode-vaapi-field');
        const vaapiHint = $('transcode-vaapi-hint');
        const showDevice = accel === 'vaapi' || accel === 'qsv';
        if (vaapiField) {
            vaapiField.classList.toggle('hidden', !showDevice);
        }
        if (vaapiHint) {
            vaapiHint.classList.toggle('hidden', !showDevice);
        }
        const deviceLabel = $('transcode-device-label');
        if (deviceLabel) {
            deviceLabel.textContent = accel === 'qsv' ? 'QSV device' : 'VAAPI device';
        }

        const selected = selectedAccelOption();
        const devices = (showDevice && selected?.devices?.length)
            ? selected.devices
            : (gpuCapabilities?.vaapiDevices || []);
        if (showDevice && devices.length) {
            replaceSelectOptions('transcode-vaapi-device', devices, $('transcode-vaapi-device')?.value);
        }

        const format = gpuCapabilities?.formats?.[accel];
        if (format) {
            applyNormalizationLimits(format);
        }
        syncNormalizationUi();
        updateEncoderHint();
    }

    function updateEncoderHint() {
        const el = $('stream-encoder-hint');
        if (!el) {
            return;
        }

        const accel = $('transcode-hwaccel')?.value || 'none';
        const codec = $('norm-video-codec')?.value || 'h264';
        if (codec === 'mpeg2') {
            if (accel === 'vaapi') {
                el.textContent = 'Encoder: mpeg2_vaapi when this GPU can encode MPEG-2, otherwise mpeg2video.';
            } else if (accel === 'qsv') {
                el.textContent = 'Encoder: mpeg2_qsv when this GPU can encode MPEG-2, otherwise mpeg2video.';
            } else {
                el.textContent = 'Encoder: mpeg2video (software). NVIDIA NVENC does not encode MPEG-2.';
            }
            return;
        }

        if (accel === 'vaapi') {
            el.textContent = 'Encoder: h264_vaapi with the H.264 profile selected above.';
            return;
        }
        if (accel === 'qsv') {
            el.textContent = 'Encoder: h264_qsv. AV1 sources decode with av1_qsv (VAAPI AV1 decode is skipped).';
            return;
        }
        if (accel === 'nvenc') {
            el.textContent = 'Encoder: h264_nvenc with the H.264 profile selected above.';
            return;
        }
        el.textContent = 'Encoder: libx264 with the H.264 profile selected above.';
    }

    function renderPipelineStatus(transcode, normalization) {
        const el = $('stream-pipeline-status');
        if (!el) {
            return;
        }

        const pipeline = transcode?.pipeline || normalization?.pipeline || {};
        const runAhead = Number(transcode?.runAheadSeconds ?? 180);
        const lines = [
            pipeline.summary || 'Live streams use the Normalization format through the Transcode encoder.'
        ];
        if (transcode?.capabilities?.summary) {
            lines.push(`GPU: ${transcode.capabilities.summary}`);
        }
        lines.push(runAhead > 0
            ? `Run-ahead buffer: ${runAhead}s`
            : 'Run-ahead buffer: off (real time)');
        if (transcode?.source) {
            lines.push(`Encoder source: ${transcode.source === 'saved' ? 'saved' : 'container environment'}`);
        }
        el.textContent = lines.join('\n');
    }

    function hardwareAccelOptions() {
        const fromGpu = gpuCapabilities?.accelerations || [];
        const byValue = {};
        fromGpu.forEach((item) => {
            if (item && item.value) {
                byValue[item.value] = item;
            }
        });
        const options = [
            { value: 'none', label: byValue.none?.label || 'Software' },
            { value: 'vaapi', label: byValue.vaapi?.label || 'Intel / AMD VAAPI' },
            { value: 'qsv', label: byValue.qsv?.label || 'Intel Quick Sync (QSV)' }
        ];
        if (byValue.nvenc) {
            options.push({ value: 'nvenc', label: byValue.nvenc.label || 'NVIDIA NVENC' });
        }
        return options;
    }

    function applyTranscodeForm(settings) {
        gpuCapabilities = settings.capabilities || gpuCapabilities;
        if ($('transcode-hwaccel')) {
            replaceSelectOptions(
                'transcode-hwaccel',
                hardwareAccelOptions(),
                settings.hardwareAcceleration || 'none'
            );
        }
        syncTranscodeUi();
        if ($('transcode-vaapi-device')) {
            $('transcode-vaapi-device').value = settings.vaapiDevice || '/dev/dri/renderD128';
        }
        if ($('transcode-runahead')) {
            $('transcode-runahead').value = String(settings.runAheadSeconds ?? 180);
        }
        syncTranscodeUi();
    }

    async function loadStreamOutput() {
        try {
            const [transcode, normalization] = await Promise.all([
                api('/transcode/settings'),
                api('/normalization/settings')
            ]);
            applyTranscodeForm(transcode);
            fillNormalizationForm(normalization);
            updateEncoderHint();
            renderPipelineStatus(transcode, normalization);
        } catch (err) {
            reportApiError(err, 'Could not load stream settings.');
        }
    }

    async function loadTranscode() {
        await loadStreamOutput();
    }

    async function saveStreamSettings() {
        await api('/transcode/settings', {
            method: 'PUT',
            body: JSON.stringify({
                hardwareAcceleration: $('transcode-hwaccel')?.value || 'none',
                videoEncoder: 'auto',
                vaapiDevice: ($('transcode-vaapi-device')?.value || '').trim(),
                runAheadSeconds: Number($('transcode-runahead')?.value || '180')
            })
        });
        await api('/normalization/settings', {
            method: 'PUT',
            body: JSON.stringify({
                resolution: $('norm-resolution')?.value || 'match',
                frameRate: $('norm-framerate')?.value || '30',
                videoCodec: $('norm-video-codec')?.value || 'h264',
                videoProfile: $('norm-video-profile')?.value || 'main',
                videoBitrate: $('norm-video-bitrate')?.value || 'auto',
                audioCodec: $('norm-audio-codec')?.value || 'aac',
                audioChannels: $('norm-audio-channels')?.value || '2.0',
                audioSampleRate: $('norm-audio-rate')?.value || '48000',
                audioBitrate: $('norm-audio-bitrate')?.value || '192k'
            })
        });
        toast('Stream settings saved. The next program on each channel uses this pipeline.', 'success');
        await loadStreamOutput();
        const result = $('transcode-test-result');
        if (result) {
            result.textContent = '';
        }
    }

    async function testTranscode() {
        const resultEl = $('transcode-test-result');
        if (resultEl) {
            resultEl.textContent = 'Running a 1-second test encode of the current Normalization target…';
        }
        const result = await api('/transcode/test', { method: 'POST' });
        if (result.ok) {
            const line = result.summary
                ? `Test encode succeeded: ${result.summary}`
                : `Test encode succeeded with ${result.encoder}.`;
            toast(line, 'success');
            if (resultEl) {
                resultEl.textContent = line;
            }
            return;
        }
        const error = result.error || `ffmpeg exited ${result.exitCode}`;
        toast(error, 'error');
        if (resultEl) {
            resultEl.textContent = error;
        }
    }

    async function resetTranscodeSettings() {
        await api('/transcode/settings', {
            method: 'PUT',
            body: JSON.stringify({ resetToEnvironment: true })
        });
        toast('Encoder reset to container environment.', 'success');
        await loadStreamOutput();
    }

    function syncNormalizationUi() {
        const mpeg2 = $('norm-video-codec')?.value === 'mpeg2';
        const profileField = $('norm-profile-field');
        if (profileField) {
            profileField.classList.toggle('hidden', mpeg2);
        }
    }

    function fillNormalizationForm(settings) {
        applyNormalizationLimits(settings.capabilities, settings);
        const fields = {
            'norm-resolution': settings.resolution || 'match',
            'norm-framerate': settings.frameRate || '30',
            'norm-video-codec': settings.videoCodec || 'h264',
            'norm-video-profile': settings.videoProfile || 'main',
            'norm-video-bitrate': settings.videoBitrate || 'auto',
            'norm-audio-codec': settings.audioCodec || 'aac',
            'norm-audio-channels': mapNormAudioChannels(settings.audioChannels),
            'norm-audio-rate': String(settings.audioSampleRate || '48000'),
            'norm-audio-bitrate': settings.audioBitrate || '192k'
        };
        Object.entries(fields).forEach(([id, value]) => {
            const el = $(id);
            if (el) {
                el.value = value;
            }
        });
        syncNormalizationUi();
        updateEncoderHint();
    }

    function mapNormAudioChannels(value) {
        const v = String(value || '2.0').toLowerCase();
        if (v === '5.1' || v === '7.1' || v === '7.2') {
            return v;
        }
        return '2.0';
    }

    async function loadNormalization() {
        await loadStreamOutput();
    }

    async function resetNormalizationSettings() {
        await api('/normalization/settings', {
            method: 'PUT',
            body: JSON.stringify({ resetToDefaults: true })
        });
        toast('Format restored to 1080p H.264 Main, 30 fps, AAC 2.0 48 kHz, clamped to this GPU.', 'success');
        await loadStreamOutput();
    }

    function normalizeQuickPin(value) {
        return String(value || '').toUpperCase().replace(/[^A-Z0-9]/g, '').slice(0, 8);
    }

    function formatQuickPin(pin) {
        const value = normalizeQuickPin(pin);
        return value.length > 4 ? `${value.slice(0, 4)}-${value.slice(4)}` : value;
    }

    function setQuickPinStatus(message, kind) {
        const status = $('quick-pin-status');
        if (!status) {
            return;
        }
        status.textContent = message || '';
        status.classList.toggle('error', kind === 'error');
    }

    function onQuickPinInput() {
        const input = $('quick-pin-input');
        if (!input) {
            return;
        }
        const formatted = formatQuickPin(input.value);
        if (input.value !== formatted) {
            input.value = formatted;
        }
        setQuickPinStatus('');
    }

    async function redeemQuickPin() {
        const pin = normalizeQuickPin($('quick-pin-input')?.value);
        if (pin.length !== 8) {
            setQuickPinStatus('Enter the 8-character pin from the app.', 'error');
            return;
        }

        const button = $('btn-quick-pin-connect');
        if (button) {
            button.disabled = true;
        }
        setQuickPinStatus('Encrypting and sending…');
        try {
            const result = await api('/quick-pin/redeem', {
                method: 'POST',
                body: JSON.stringify({ pin })
            });
            setQuickPinStatus(result.message || 'Sent.');
            toast(result.message || 'Sent.', 'success');
            const input = $('quick-pin-input');
            if (input) {
                input.value = '';
            }
        } catch (err) {
            setQuickPinStatus(err.message || 'Could not send to the pin server.', 'error');
            toast(err.message || 'Could not send to the pin server.', 'error');
        } finally {
            if (button) {
                button.disabled = false;
            }
        }
    }

    async function loadGeneral() {
        try {
            const [settings, timeZones] = await Promise.all([
                api('/general/settings'),
                api('/general/timezones')
            ]);

            const tzSelect = $('general-schedule-tz');
            if (tzSelect) {
                const selected = settings.scheduleTimeZone || 'America/New_York';
                setScheduleTimeZone(selected);
                const options = Array.isArray(timeZones) ? timeZones : [];
                const hasSelected = options.some((tz) => tz.id === selected);

                tzSelect.innerHTML = '';
                if (!hasSelected && selected) {
                    const legacy = document.createElement('option');
                    legacy.value = selected;
                    legacy.textContent = `${selected} (saved — pick a listed zone)`;
                    legacy.selected = true;
                    tzSelect.appendChild(legacy);
                }

                options.forEach((tz) => {
                    const option = document.createElement('option');
                    option.value = tz.id;
                    option.textContent = tz.label || `${tz.id} (${tz.offset || ''})`;
                    if (tz.id === selected) {
                        option.selected = true;
                    }
                    tzSelect.appendChild(option);
                });

                if (!tzSelect.value && options.length > 0) {
                    tzSelect.value = options[0].id;
                }
            }

            if ($('general-debug-logging')) {
                $('general-debug-logging').checked = !!settings.debugLogging;
            }
            if ($('general-playout-days')) {
                $('general-playout-days').value = String(settings.playoutDaysToBuild ?? 14);
            }
            if ($('general-stream-idle-timeout')) {
                $('general-stream-idle-timeout').value = String(settings.streamIdleTimeoutSeconds ?? 30);
            }
            if ($('general-public-url')) {
                $('general-public-url').value = settings.publicBaseUrl || '';
            }
            if ($('general-local-url')) {
                $('general-local-url').value = settings.localBaseUrl || '';
            }
            await loadClientLogList();
        } catch (err) {
            reportApiError(err, 'Could not load general settings.');
        }
    }

    async function saveGeneralSettings() {
        if (!syncConfigPage()) {
            toast('ChannelFlow-Server is not ready. Reload the page.', 'error');
            return;
        }

        try {
            const saved = await api('/general/settings', {
                method: 'PUT',
                body: JSON.stringify({
                    debugLogging: !!$('general-debug-logging')?.checked,
                    scheduleTimeZone: $('general-schedule-tz')?.value || 'America/New_York',
                    playoutDaysToBuild: Number($('general-playout-days')?.value || '14'),
                    streamIdleTimeoutSeconds: Number($('general-stream-idle-timeout')?.value || '30'),
                    publicBaseUrl: ($('general-public-url')?.value || '').trim(),
                    localBaseUrl: ($('general-local-url')?.value || '').trim()
                })
            });
            if ($('general-public-url')) {
                $('general-public-url').value = saved.publicBaseUrl || '';
            }
            if ($('general-local-url')) {
                $('general-local-url').value = saved.localBaseUrl || '';
            }
            toast('General settings saved.', 'success');
            await loadGeneral();
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    function formatClientWhen(value) {
        if (!value) {
            return 'Never';
        }
        const date = new Date(value);
        if (Number.isNaN(date.getTime())) {
            return String(value);
        }
        return date.toLocaleString();
    }

    async function loadPairedClients() {
        const el = $('clients-table');
        if (!el) {
            return;
        }

        try {
            const data = await api('/clients');
            const clients = Array.isArray(data?.clients) ? data.clients : [];
            if (!clients.length) {
                el.innerHTML = '<p class="hint">No ChannelFlow TV apps are paired yet. Connect one from Quick Pin.</p>';
                return;
            }

            el.innerHTML = `<table class="data-table">
                <thead><tr><th>Name</th><th>App</th><th>Last seen</th><th>Paired</th><th>Key</th><th></th></tr></thead>
                <tbody>${clients.map((client) => {
                    const name = escapeHtml(client.deviceName || client.deviceId || 'ChannelFlow TV');
                    const app = escapeHtml([client.appVersion, client.osVersion].filter(Boolean).join(' · ') || '—');
                    return `<tr>
                        <td>${name}</td>
                        <td>${app}</td>
                        <td>${escapeHtml(formatClientWhen(client.lastSeenAt))}</td>
                        <td>${escapeHtml(formatClientWhen(client.createdAt))}</td>
                        <td><code>${escapeHtml(client.keyHint || '••••')}</code></td>
                        <td><button type="button" class="btn-danger" data-remove-client="${escapeHtml(client.id)}">Remove</button></td>
                    </tr>`;
                }).join('')}</tbody>
            </table>`;
        } catch (err) {
            reportApiError(err, 'Could not load paired clients.');
        }
    }

    async function removePairedClient(id) {
        if (!id) {
            return;
        }
        if (!confirm('Remove this ChannelFlow TV client? Its API key is destroyed. The next time that TV tries to connect, it cannot, and it will drop this server from its list.')) {
            return;
        }

        await api('/clients/' + encodeURIComponent(id), { method: 'DELETE' });
        toast('Client removed.', 'success');
        await loadPairedClients();
    }

    let clientLogDevices = [];

    function selectedClientLogDeviceId() {
        return $('client-logs-device')?.value || '';
    }

    function selectedClientLogFile() {
        return $('client-logs-file')?.value || '';
    }

    function fillClientLogDevices(devices, keepId) {
        const select = $('client-logs-device');
        if (!select) {
            return;
        }

        const preferred = keepId || select.value;
        select.innerHTML = '';
        if (!devices.length) {
            const empty = document.createElement('option');
            empty.value = '';
            empty.textContent = 'No clients yet';
            select.appendChild(empty);
            return;
        }

        devices.forEach((device) => {
            const option = document.createElement('option');
            option.value = device.deviceId;
            const label = device.deviceName || device.deviceId;
            const extra = [device.appVersion, device.osVersion].filter(Boolean).join(' · ');
            option.textContent = extra ? `${label} (${extra})` : label;
            select.appendChild(option);
        });

        if (preferred && devices.some((device) => device.deviceId === preferred)) {
            select.value = preferred;
        }
    }

    function fillClientLogFiles(files, keepName) {
        const select = $('client-logs-file');
        if (!select) {
            return;
        }

        const preferred = keepName || select.value;
        select.innerHTML = '';
        if (!files.length) {
            const empty = document.createElement('option');
            empty.value = '';
            empty.textContent = 'No log files';
            select.appendChild(empty);
            return;
        }

        files.forEach((file) => {
            const option = document.createElement('option');
            option.value = file.name;
            option.textContent = file.name;
            select.appendChild(option);
        });

        if (preferred && files.some((file) => file.name === preferred)) {
            select.value = preferred;
        }
    }

    async function loadClientLogList() {
        const view = $('client-logs-view');
        try {
            const payload = await api('/client-logs');
            clientLogDevices = Array.isArray(payload?.devices) ? payload.devices : [];
            fillClientLogDevices(clientLogDevices);
            if (!clientLogDevices.length) {
                fillClientLogFiles([]);
                if (view) {
                    view.textContent = 'No ChannelFlow TV clients have sent logs yet.';
                }
                return;
            }

            await loadClientLogContents();
        } catch (err) {
            fillClientLogDevices([]);
            fillClientLogFiles([]);
            if (view) {
                view.textContent = err.message || 'Could not load client logs.';
            }
        }
    }

    async function loadClientLogContents() {
        const view = $('client-logs-view');
        const deviceId = selectedClientLogDeviceId();
        if (!deviceId) {
            fillClientLogFiles([]);
            if (view) {
                view.textContent = 'No ChannelFlow TV clients have sent logs yet.';
            }
            return;
        }

        const params = new URLSearchParams();
        const file = selectedClientLogFile();
        if (file) {
            params.set('file', file);
        }
        const query = params.toString();
        const detail = await api('/client-logs/' + encodeURIComponent(deviceId) + (query ? '?' + query : ''));
        fillClientLogFiles(Array.isArray(detail?.files) ? detail.files : [], detail?.file);
        if (view) {
            view.textContent = detail?.content || 'This file is empty.';
            view.scrollTop = view.scrollHeight;
        }
    }

    function normalizeConfigPageRoot(node) {
        if (!node) {
            return null;
        }

        if (node.id === 'ChannelFlowConfigPage') {
            return node;
        }

        if (typeof node.querySelector === 'function') {
            const nested = node.querySelector('#ChannelFlowConfigPage');
            if (nested) {
                return nested;
            }
        }

        if (typeof node.closest === 'function') {
            return node.closest('#ChannelFlowConfigPage');
        }

        return null;
    }

    function syncConfigPage(preferred) {
        const resolved = resolveConfigPage(preferred || configPage);
        if (resolved) {
            configPage = resolved;
        }

        return configPage;
    }

    async function loadLists() {
        try {
            await ensureFinTvLists(true);
            renderListsTable();
            if (listPage !== 'lists') {
                await loadMusicVideoListPage();
            }
        } catch (err) {
            reportApiError(err, 'Could not load ChannelFlow lists.');
        }
    }

    let listPage = 'lists';

    function applyListPage() {
        document.querySelectorAll('#list-inner-tabs .inner-tab').forEach((tab) => {
            const on = tab.dataset.listPage === listPage;
            tab.classList.toggle('active', on);
            tab.setAttribute('aria-selected', on ? 'true' : 'false');
        });
        document.querySelectorAll('#tab-list .list-page').forEach((page) => {
            const on = page.id === 'list-page-' + listPage;
            page.classList.toggle('hidden', !on);
            page.hidden = !on;
        });
        if (listPage !== 'lists') {
            loadMusicVideoListPage().catch((err) => toast(err.message, 'error'));
        }
    }

    async function loadMusicVideoListPage() {
        const channels = await api('/lists/music-video-channels') || [];
        if (listPage === 'artists') {
            await renderMusicVideoArtists(channels);
        } else if (listPage === 'youtube') {
            await renderMusicVideoYoutube(channels);
        }
    }

    async function renderMusicVideoArtists(channels) {
        const el = $('mv-artists-panels');
        if (!el) {
            return;
        }

        if (!channels.length) {
            el.innerHTML = '<div class="empty-state">No music-video channels found.</div>';
            return;
        }

        const blocks = await Promise.all(channels.map(async (ch) => {
            const artists = await api('/lists/music-video-channels/' + ch.id + '/artists') || [];
            const hint = ch.libraryTag === 'channelflow-music-video' && !artists.length
                ? '<p class="hint">Empty: plays every catalog artist not listed on The Parody Channel or Rap On Tap.</p>'
                : '';
            const rows = artists.length
                ? artists.map((row) => `<div class="list-card"><div><strong>${escapeHtml(row.artistName)}</strong></div><div class="row-actions"><button type="button" data-mv-del-artist="${row.id}" data-channel="${ch.id}">Remove</button></div></div>`).join('')
                : '<div class="empty-state">No artists added yet.</div>';
            return `<div class="card section-card" data-mv-channel="${ch.id}">
                <h4>${escapeHtml(ch.name)}</h4>
                ${hint}
                <div class="actions">
                    <input class="emby-input mv-artist-input" data-channel="${ch.id}" placeholder="Artist name">
                    <button type="button" class="emby-button" data-mv-add-artist="${ch.id}">Add artist</button>
                </div>
                ${rows}
            </div>`;
        }));
        el.innerHTML = blocks.join('');
        el.querySelectorAll('[data-mv-add-artist]').forEach((btn) => {
            btn.onclick = () => addMusicVideoArtist(btn.dataset.mvAddArtist);
        });
        el.querySelectorAll('[data-mv-del-artist]').forEach((btn) => {
            btn.onclick = () => deleteMusicVideoArtist(btn.dataset.channel, btn.dataset.mvDelArtist);
        });
    }

    async function addMusicVideoArtist(channelId) {
        const input = q(`[data-mv-channel="${channelId}"] .mv-artist-input`);
        const name = (input?.value || '').trim();
        if (!name) {
            toast('Enter an artist name.', 'info');
            return;
        }

        await api('/lists/music-video-channels/' + channelId + '/artists', {
            method: 'POST',
            body: JSON.stringify({ artistName: name })
        });
        if (input) {
            input.value = '';
        }
        toast('Artist added.', 'success');
        await loadMusicVideoListPage();
    }

    async function deleteMusicVideoArtist(channelId, artistId) {
        await api('/lists/music-video-channels/' + channelId + '/artists/' + artistId, { method: 'DELETE' });
        toast('Artist removed.', 'success');
        await loadMusicVideoListPage();
    }

    async function renderMusicVideoYoutube(channels) {
        const el = $('mv-youtube-panels');
        if (!el) {
            return;
        }

        if (!channels.length) {
            el.innerHTML = '<div class="empty-state">No music-video channels found.</div>';
            return;
        }

        const blocks = await Promise.all(channels.map(async (ch) => {
            const sources = await api('/lists/music-video-channels/' + ch.id + '/youtube') || [];
            const rows = sources.length
                ? sources.map((row) => `<div class="list-card"><div><strong>${escapeHtml(row.title || row.sourceUrl)}</strong><div class="meta">${escapeHtml(row.sourceUrl)}${row.isPlaylist ? ' · playlist' : ''}</div></div><div class="row-actions"><button type="button" data-mv-del-yt="${row.id}" data-channel="${ch.id}">Remove</button></div></div>`).join('')
                : '<div class="empty-state">No YouTube sources yet.</div>';
            return `<div class="card section-card" data-mv-yt-channel="${ch.id}">
                <h4>${escapeHtml(ch.name)}</h4>
                <div class="actions">
                    <input class="emby-input mv-yt-input" data-channel="${ch.id}" placeholder="https://www.youtube.com/playlist?list=… or a video URL">
                    <button type="button" class="emby-button" data-mv-add-yt="${ch.id}">Import</button>
                </div>
                ${rows}
            </div>`;
        }));
        el.innerHTML = blocks.join('');
        el.querySelectorAll('[data-mv-add-yt]').forEach((btn) => {
            btn.onclick = () => addMusicVideoYoutube(btn.dataset.mvAddYt);
        });
        el.querySelectorAll('[data-mv-del-yt]').forEach((btn) => {
            btn.onclick = () => deleteMusicVideoYoutube(btn.dataset.channel, btn.dataset.mvDelYt);
        });
    }

    async function addMusicVideoYoutube(channelId) {
        const input = q(`[data-mv-yt-channel="${channelId}"] .mv-yt-input`);
        const url = (input?.value || '').trim();
        if (!url) {
            toast('Paste a YouTube video or playlist URL.', 'info');
            return;
        }

        await api('/lists/music-video-channels/' + channelId + '/youtube', {
            method: 'POST',
            body: JSON.stringify({ url })
        });
        if (input) {
            input.value = '';
        }
        toast('YouTube source imported.', 'success');
        await loadMusicVideoListPage();
    }

    async function deleteMusicVideoYoutube(channelId, sourceId) {
        await api('/lists/music-video-channels/' + channelId + '/youtube/' + sourceId, { method: 'DELETE' });
        toast('YouTube source removed.', 'success');
        await loadMusicVideoListPage();
    }

    function selectedLibraryIds(containerId) {
        const el = $(containerId);
        if (!el) {
            return [];
        }

        return Array.from(el.querySelectorAll('input[type="checkbox"]:checked'))
            .flatMap((input) => String(input.dataset.libIds || input.dataset.libId || '').split(','))
            .map((id) => id.trim())
            .filter(Boolean);
    }

    function libraryMemberIds(lib) {
        if (Array.isArray(lib.ids) && lib.ids.length) {
            return lib.ids.map(String);
        }

        return lib.id ? [String(lib.id)] : [];
    }

    function renderJellyfinLibraryGroup(containerId, libraries, selectedIds, group) {
        const el = $(containerId);
        if (!el) {
            return;
        }

        const matching = (libraries || []).filter((lib) => (lib.groups || []).includes(group));
        if (!matching.length) {
            el.innerHTML = '<div class="empty-state">No matching libraries in the synced catalog yet.</div>';
            return;
        }

        const selected = new Set((selectedIds || []).map(String));
        el.innerHTML = matching.map((lib) => {
            const count = lib.itemCount || 0;
            const memberIds = libraryMemberIds(lib);
            const checked = memberIds.some((id) => selected.has(id)) ? ' checked' : '';
            return `<label class="field checkbox-field">
                <input type="checkbox" data-lib-id="${escapeHtml(String(lib.id || memberIds[0] || ''))}" data-lib-ids="${escapeHtml(memberIds.join(','))}"${checked}>
                <span class="channelflow-check-box" aria-hidden="true"></span>
                <span>${escapeHtml(lib.name)} <span class="library-pick-meta">${count} item${count === 1 ? '' : 's'}</span></span>
            </label>`;
        }).join('');
    }

    let mediaServers = [];
    let libraryPage = 'connections';
    const libraryKindSelected = {};

    const LIBRARY_KIND_PAGES = ['jellyfin', 'emby', 'plex', 'sidecar', 'other'];
    const LIBRARY_PAGES = ['connections', 'jellyfin', 'emby', 'plex', 'sidecar', 'other', 'removed'];

    function libraryPath(page) {
        const name = LIBRARY_PAGES.includes(page) ? page : 'connections';
        return '/library/' + name;
    }

    function libraryPageFromPath(pathname) {
        const path = normalizePathname(pathname);
        if (path === '/library' || path === '/jellyfin') {
            return 'connections';
        }

        if (path.startsWith('/library/')) {
            const page = path.slice('/library/'.length);
            if (LIBRARY_PAGES.includes(page)) {
                return page;
            }
        }

        return 'connections';
    }

    function librarySubtitle() {
        const labels = {
            connections: TAB_SUBTITLES.jellyfin,
            jellyfin: 'Jellyfin libraries and synced catalog',
            emby: 'Emby libraries and synced catalog',
            plex: 'Plex libraries and synced catalog',
            sidecar: 'Local sidecar folders and NFO catalog',
            other: 'Other media sources',
            removed: 'Items missing from the last catalog sync'
        };
        return labels[libraryPage] || TAB_SUBTITLES.jellyfin;
    }

    function applyLibraryPage(options) {
        options = options || {};
        const before = libraryPage;
        syncLibraryKindTabs();
        if (libraryPage !== before && !options.skipUrl) {
            history.replaceState({ tab: 'jellyfin', libraryPage }, '', withAppBase(libraryPath(libraryPage)));
        }

        document.querySelectorAll('#library-inner-tabs .inner-tab').forEach((tab) => {
            const on = tab.dataset.libraryPage === libraryPage;
            tab.classList.toggle('active', on);
            tab.setAttribute('aria-selected', on ? 'true' : 'false');
            tab.setAttribute('href', withAppBase(libraryPath(tab.dataset.libraryPage)));
        });
        document.querySelectorAll('#tab-jellyfin .library-page').forEach((page) => {
            const on = page.id === 'library-page-' + libraryPage;
            page.classList.toggle('hidden', !on);
            page.hidden = !on;
        });
        if (libraryPage === 'removed') {
            loadLibraryRemoved();
        } else if (libraryPage !== 'connections') {
            renderKindPanel(libraryPage);
        }
    }

    function switchLibraryPage(page, options) {
        options = options || {};
        libraryPage = LIBRARY_PAGES.includes(page) ? page : 'connections';
        applyLibraryPage({ skipUrl: true });
        const path = libraryPath(libraryPage);
        if (!options.skipHistory && normalizePathname(location.pathname) !== path) {
            history.pushState({ tab: 'jellyfin', libraryPage }, '', withAppBase(path));
        }

        window.dispatchEvent(new CustomEvent('channelflow-tabchange', {
            detail: { tab: 'jellyfin', title: TAB_TITLES.jellyfin, subtitle: librarySubtitle() }
        }));
    }

    function usedLibraryKinds() {
        return new Set((mediaServers || [])
            .filter((server) => server.enabled !== false)
            .map((server) => server.kind));
    }

    function syncLibraryKindTabs() {
        const used = usedLibraryKinds();
        document.querySelectorAll('#library-inner-tabs .inner-tab').forEach((tab) => {
            const page = tab.dataset.libraryPage;
            const always = page === 'connections' || page === 'removed';
            const show = always || used.has(page);
            tab.hidden = !show;
            tab.classList.toggle('hidden', !show);
        });
        if (LIBRARY_KIND_PAGES.includes(libraryPage) && !used.has(libraryPage)) {
            libraryPage = 'connections';
        }
    }

    function healthBadge(server) {
        if (server.lastHealthOk === true) {
            return '<span class="ms-health ok">Healthy</span>';
        }
        if (server.lastHealthOk === false) {
            return '<span class="ms-health bad">Unreachable</span>';
        }
        return '<span class="ms-health">Not tested</span>';
    }

    function renderConnectionCards() {
        const el = $('ms-connection-list');
        if (!el) {
            return;
        }
        if (!mediaServers.length) {
            el.innerHTML = '<div class="card section-card"><p class="hint">No media servers yet. Add one above.</p></div>';
            return;
        }
        el.innerHTML = mediaServers.map((server) => {
            const isSidecar = server.kind === 'sidecar';
            return `<div class="card section-card ms-server-card" data-id="${server.id}">
                <div class="section-header">
                    <h3>${escapeHtml(server.name || server.kind)} ${healthBadge(server)}</h3>
                    <div class="actions">
                        <button type="button" class="emby-button" data-ms="test">Test server</button>
                        <button type="button" class="emby-button" data-ms="health">Health</button>
                        <button type="button" class="raised button-submit emby-button" data-ms="save">Save</button>
                        <button type="button" class="emby-button" data-ms="delete">Remove</button>
                    </div>
                </div>
                <p class="hint">${escapeHtml(server.kind)} ${server.lastHealthMessage ? '· ' + escapeHtml(server.lastHealthMessage) : ''}</p>
                <div class="library-add-grid">
                    <label class="field"><span>Name</span><input class="emby-input ms-edit-name" value="${escapeHtml(server.name || '')}"></label>
                    ${isSidecar
                        ? `<label class="field"><span>Local folder</span><input class="emby-input ms-edit-root" value="${escapeHtml(server.sidecarRoot || '')}"></label>`
                        : `<label class="field"><span>URL</span><input class="emby-input ms-edit-url" value="${escapeHtml(server.baseUrl || '')}"></label>
                           <label class="field"><span>API key / token</span><input class="emby-input ms-edit-token" placeholder="${server.hasToken ? 'saved · leave blank to keep' : ''}" autocomplete="off"></label>`}
                </div>
                <h4>Path remaps</h4>
                <p class="hint">Jellyfin path prefix on the left, the folder this ChannelFlow can read on the right. One line: <code>/media = /srv/Tower/media</code></p>
                <textarea class="ms-remap" rows="4" placeholder="/media = /srv/Tower/media"></textarea>
                <div class="actions">
                    <button type="button" class="emby-button" data-ms="save-remap">Save remaps</button>
                    <button type="button" class="emby-button" data-ms="test-remap">Test remaps</button>
                </div>
                <pre class="ms-remap-result hint"></pre>
            </div>`;
        }).join('');
        el.querySelectorAll('.ms-server-card').forEach((card) => bindServerCard(card));
    }

    function bindServerCard(card) {
        const id = card.dataset.id;
        const remapEl = card.querySelector('.ms-remap');
        api('/media-servers/' + id + '/path-mappings').then((rows) => {
            remapEl.value = (rows || []).map((r) => r.jellyfinPrefix + ' = ' + r.localPrefix).join('\n');
        }).catch(() => {});
        card.querySelector('[data-ms="test"]').onclick = () => runServerAction(id, 'test', card);
        card.querySelector('[data-ms="health"]').onclick = () => runServerAction(id, 'health', card);
        card.querySelector('[data-ms="save"]').onclick = () => saveServerCard(id, card);
        card.querySelector('[data-ms="delete"]').onclick = () => deleteServer(id);
        card.querySelector('[data-ms="save-remap"]').onclick = () => saveServerRemaps(id, card);
        card.querySelector('[data-ms="test-remap"]').onclick = () => testServerRemaps(id, card);
    }

    async function runServerAction(id, action, card) {
        try {
            const result = await api('/media-servers/' + id + '/' + action, { method: 'POST' });
            toast((result.health && result.health.message) || 'Done.', result.health && result.health.ok ? 'success' : 'error');
            await loadMediaServers();
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function saveServerCard(id, card) {
        const body = {
            name: card.querySelector('.ms-edit-name')?.value,
            baseUrl: card.querySelector('.ms-edit-url')?.value,
            sidecarRoot: card.querySelector('.ms-edit-root')?.value,
            accessToken: card.querySelector('.ms-edit-token')?.value || undefined
        };
        await api('/media-servers/' + id, { method: 'PUT', body: JSON.stringify(body) });
        toast('Server saved.', 'success');
        await loadMediaServers();
    }

    async function deleteServer(id) {
        if (!confirm('Remove this media server? Catalog items stay until cleanup.')) {
            return;
        }
        await api('/media-servers/' + id, { method: 'DELETE' });
        await loadMediaServers();
    }

    async function saveServerRemaps(id, card) {
        const mappings = (card.querySelector('.ms-remap').value || '').split('\n')
            .map((line) => line.split('='))
            .filter((p) => p.length >= 2)
            .map((p, i) => ({ jellyfinPrefix: p[0].trim(), localPrefix: p.slice(1).join('=').trim(), sortOrder: i }));
        await api('/media-servers/' + id + '/path-mappings', { method: 'PUT', body: JSON.stringify(mappings) });
        toast('Remaps saved.', 'success');
    }

    async function testServerRemaps(id, card) {
        const result = await api('/media-servers/' + id + '/path-mappings/test', { method: 'POST' });
        card.querySelector('.ms-remap-result').textContent = JSON.stringify(result, null, 2);
    }

    async function loadMediaServers() {
        const data = await api('/media-servers') || {};
        mediaServers = data.servers || [];
        const url = $('ms-url');
        if (url && !url.value && data.suggestedJellyfinUrl) {
            url.value = data.suggestedJellyfinUrl;
        }
        syncLibraryKindTabs();
        renderConnectionCards();
        if (libraryPage !== 'connections' && libraryPage !== 'removed') {
            renderKindPanel(libraryPage);
        }
    }

    function serversOfKind(kind) {
        return mediaServers.filter((s) => s.kind === kind && s.enabled !== false);
    }

    function renderKindPanel(kind) {
        const el = $('ms-kind-' + kind);
        if (!el) {
            return;
        }
        const servers = serversOfKind(kind);
        if (!servers.length) {
            const placeholder = kind === 'emby' || kind === 'plex'
                ? `<p class="hint">${kind === 'plex' ? 'Plex' : 'Emby'} support is a placeholder. Add a connection on the Connections tab (URL and token). Test/health work; catalog sync comes later.</p>`
                : '<p class="hint">Add a ' + kind + ' connection on the Connections tab.</p>';
            el.innerHTML = '<div class="card section-card">' + placeholder + '</div>';
            return;
        }
        const selectedId = libraryKindSelected[kind] || servers[0].id;
        libraryKindSelected[kind] = selectedId;
        const selected = servers.find((s) => s.id === selectedId) || servers[0];
        const canSync = !!selected.canSync;
        el.innerHTML = `<div class="card section-card">
                <div class="section-header">
                    <h3>${escapeHtml(kind)}</h3>
                    <div class="actions">
                        ${servers.length > 1 ? `<select class="emby-select" id="ms-pick-${kind}">${servers.map((s) =>
                            `<option value="${s.id}" ${s.id === selected.id ? 'selected' : ''}>${escapeHtml(s.name)}</option>`).join('')}</select>` : ''}
                        <button type="button" class="emby-button" id="ms-refresh-${kind}">Refresh libraries</button>
                        <button type="button" class="raised button-submit emby-button" id="ms-sync-${kind}" ${canSync ? '' : 'disabled'}>${canSync ? 'Sync catalog' : 'Sync coming later'}</button>
                        <button type="button" class="emby-button" id="ms-save-libs-${kind}">Save libraries</button>
                    </div>
                </div>
                <p class="hint">${canSync
                    ? 'Check libraries to import. Sync pulls metadata (and sidecar .nfo files) into ChannelFlow.'
                    : 'This server type is connected for health checks. Catalog browse/sync is not available yet.'}</p>
                <p class="hint" id="ms-kind-status-${kind}">${healthBadge(selected)} ${escapeHtml(selected.lastHealthMessage || '')}</p>
                <div class="jellyfin-library-grid" id="ms-libs-${kind}"></div>
                <h4>Path remaps</h4>
                <p class="hint">Server path prefix → local ChannelFlow mount. One mapping per line: <code>/data/media = /media</code></p>
                <textarea class="ms-remap" id="ms-remap-${kind}" rows="4" placeholder="/data/media = /media"></textarea>
                <div class="actions">
                    <button type="button" class="emby-button" id="ms-save-remap-${kind}">Save remaps</button>
                    <button type="button" class="emby-button" id="ms-test-remap-${kind}">Test remaps</button>
                </div>
                <pre class="ms-remap-result hint" id="ms-remap-result-${kind}"></pre>
            </div>
            <div class="card section-card"><h3>TV shows</h3><p class="hint">Click a show, then a season, to see its episodes.</p><div id="ms-media-${kind}-tv" class="data-table-wrap catalog-media-table"></div></div>
            <div class="card section-card"><h3>Movies</h3><div id="ms-media-${kind}-movies" class="data-table-wrap catalog-media-table"></div></div>
            <div class="card section-card"><h3>Music</h3><p class="hint">Click an artist to see their songs.</p><div id="ms-media-${kind}-music" class="data-table-wrap catalog-media-table"></div></div>
            <div class="card section-card"><h3>Music videos</h3><p class="hint">Click a star to see their music videos.</p><div id="ms-media-${kind}-musicvideos" class="data-table-wrap catalog-media-table"></div></div>
            <div class="card section-card"><h3>Past Tense News</h3><p class="hint">Jellyfin library named Past Tense News (Home Videos and Photos). Enable it under Home movies / news, then sync.</p><div id="ms-media-${kind}-news" class="data-table-wrap catalog-media-table"></div></div>`;
        renderServerLibraries(kind, selected);
        const pick = $('ms-pick-' + kind);
        if (pick) {
            pick.onchange = () => {
                libraryKindSelected[kind] = pick.value;
                renderKindPanel(kind);
            };
        }
        $('ms-refresh-' + kind).onclick = () => refreshServerLibraries(selected.id, kind);
        $('ms-sync-' + kind).onclick = () => syncServerCatalog(selected.id, kind);
        $('ms-save-libs-' + kind).onclick = () => saveServerLibraries(selected.id, kind);
        bindKindRemaps(kind, selected.id);
        loadKindCatalog(kind, selected.id);
    }

    function renderServerLibraries(kind, server) {
        const el = $('ms-libs-' + kind);
        const libs = server.libraries || [];
        if (!libs.length) {
            el.innerHTML = '<div class="empty-state">No libraries yet. Click Refresh libraries.</div>';
            return;
        }
        const groups = [
            ['tv', 'TV'],
            ['movies', 'Movies'],
            ['music', 'Music'],
            ['musicvideos', 'Music videos'],
            ['news', 'Past Tense News'],
            ['other', 'Other']
        ];
        el.innerHTML = groups.map(([group, label]) => {
            const matching = libs.filter((lib) => (lib.group || 'other') === group);
            if (!matching.length) {
                return '';
            }
            return `<section class="library-pick-card"><h4>${label}</h4><div class="library-pick-list">${matching.map((lib) => {
                const checked = lib.syncEnabled ? 'checked' : '';
                return `<label class="checkbox-field"><input type="checkbox" data-lib-id="${lib.id}" ${checked}>
                    <span class="channelflow-check-box" aria-hidden="true"></span>
                    <span>${escapeHtml(lib.name)} <span class="library-pick-meta">${lib.itemCount || 0}</span></span></label>`;
            }).join('')}</div></section>`;
        }).join('') || '<div class="empty-state">No libraries on this server.</div>';
        decorateCheckboxes();
    }

    function bindKindRemaps(kind, id) {
        const card = $('ms-kind-' + kind)?.querySelector('.card');
        const remapEl = $('ms-remap-' + kind);
        if (!card || !remapEl) {
            return;
        }
        api('/media-servers/' + id + '/path-mappings').then((rows) => {
            remapEl.value = (rows || []).map((r) => r.jellyfinPrefix + ' = ' + r.localPrefix).join('\n');
        }).catch(() => {});
        $('ms-save-remap-' + kind).onclick = () => saveServerRemaps(id, card);
        $('ms-test-remap-' + kind).onclick = () => testServerRemaps(id, card);
    }

    async function refreshServerLibraries(id, kind) {
        try {
            await api('/media-servers/' + id + '/libraries/refresh', { method: 'POST' });
            await loadMediaServers();
            renderKindPanel(kind);
            toast('Libraries refreshed.', 'success');
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function saveServerLibraries(id, kind) {
        const el = $('ms-libs-' + kind);
        const libraries = Array.from(el.querySelectorAll('input[data-lib-id]')).map((input) => ({
            id: input.dataset.libId,
            syncEnabled: input.checked
        }));
        await api('/media-servers/' + id + '/libraries', { method: 'PUT', body: JSON.stringify(libraries) });
        toast('Libraries saved.', 'success');
        await loadMediaServers();
        renderKindPanel(kind);
    }

    function formatSyncCount(value) {
        return Number(value || 0).toLocaleString();
    }

    function catalogSyncFinishedRecently(snap) {
        const at = Date.parse(snap && snap.finishedAt ? snap.finishedAt : '');
        return Number.isFinite(at) && (Date.now() - at) < 120000;
    }

    let taskPopupStackBound = false;

    function taskPopupStackEl() {
        return document.getElementById('task-popup-stack');
    }

    function taskPopupEl(kind) {
        return document.getElementById('task-run-popup-' + kind);
    }

    function hideTaskRunPopup(kind) {
        if (!kind) {
            Object.keys(libraryTaskHideTimers).forEach((key) => hideTaskRunPopup(key));
            writeLibraryTaskSnaps([]);
            return;
        }

        if (libraryTaskHideTimers[kind]) {
            clearTimeout(libraryTaskHideTimers[kind]);
            delete libraryTaskHideTimers[kind];
        }

        delete libraryTaskExpecting[kind];
        delete libraryTaskSeen[kind];
        delete libraryTaskWasRunning[kind];
        const el = taskPopupEl(kind);
        if (el) {
            el.remove();
        }

        writeLibraryTaskSnaps(readLibraryTaskSnaps().filter((snap) => snap.kind !== kind));
    }

    function libraryTaskFinishedRecently(status) {
        const at = Date.parse(status && (status.finishedAt || status.lastCompletedAt) ? (status.finishedAt || status.lastCompletedAt) : '');
        return Number.isFinite(at) && (Date.now() - at) < 120000;
    }

    function libraryTaskCompletedAt(status) {
        const at = Date.parse(status && (status.lastCompletedAt || status.finishedAt) ? (status.lastCompletedAt || status.finishedAt) : '');
        return Number.isFinite(at) ? at : 0;
    }

    function libraryTaskStartedAt(status, kind) {
        const at = Date.parse(status && status.lastStartedAt ? status.lastStartedAt : '');
        if (Number.isFinite(at) && at > 0) {
            return at;
        }

        return (kind && libraryTaskExpecting[kind]) || 0;
    }

    function thisLibraryTaskRunFinished(status, kind) {
        if (!status || status.isRunning) {
            return false;
        }

        const completed = libraryTaskCompletedAt(status);
        if (!completed) {
            return false;
        }

        const started = libraryTaskStartedAt(status, kind);
        if (started && completed < started - 2000) {
            return false;
        }

        if (kind && libraryTaskExpecting[kind] && completed < libraryTaskExpecting[kind] - 2000) {
            return false;
        }

        return true;
    }

    function libraryTaskStillPending(status, kind) {
        if (kind && libraryTaskClosedKinds[kind]) {
            return false;
        }

        if (status && status.isRunning) {
            return true;
        }

        if (kind && libraryTaskExpecting[kind] && !thisLibraryTaskRunFinished(status, kind)) {
            return true;
        }

        return false;
    }

    function readLibraryTaskSnaps() {
        try {
            const raw = sessionStorage.getItem(LIBRARY_TASK_SNAP_KEY);
            const parsed = raw ? JSON.parse(raw) : null;
            if (Array.isArray(parsed)) {
                return parsed.filter((snap) => snap && snap.kind);
            }

            return parsed && parsed.kind ? [parsed] : [];
        } catch (ignore) {
            return [];
        }
    }

    function writeLibraryTaskSnaps(snaps) {
        try {
            const keep = (snaps || []).filter((snap) => snap && snap.status && (
                snap.status.isRunning
                || snap.phase === 'running'
                || snap.phase === 'done'
                || snap.phase === 'error'
            ));
            if (!keep.length) {
                sessionStorage.removeItem(LIBRARY_TASK_SNAP_KEY);
                return;
            }

            sessionStorage.setItem(LIBRARY_TASK_SNAP_KEY, JSON.stringify(keep));
        } catch (ignore) {
            // Private mode or quota — polling still works after load.
        }
    }

    function libraryTaskMeta(status) {
        if (!status) {
            return '';
        }

        if (status.isRunning && status.total) {
            return formatSyncCount(status.processed) + ' of ' + formatSyncCount(status.total);
        }

        if (!status.isRunning && status.lastCompletedAt) {
            if (status.lastWithChapters != null) {
                return formatSyncCount(status.lastWithChapters) + ' with chapters';
            }

            if (status.lastMeasured != null) {
                return formatSyncCount(status.lastMeasured) + ' measured';
            }
        }

        return status.percent != null ? status.percent + '%' : '';
    }

    function mapCleanupTask(status) {
        if (!status) {
            return null;
        }

        const local = status.localScan || {};
        if (local.isRunning) {
            return {
                isRunning: true,
                lastStartedAt: local.lastStartedAt,
                lastCompletedAt: local.lastCompletedAt,
                lastError: local.lastError,
                processed: local.processedItems,
                total: local.totalItems,
                percent: local.totalItems
                    ? Math.max(0, Math.min(100, Math.round(100 * (local.processedItems || 0) / local.totalItems)))
                    : null,
                message: 'Scanning remapped local files…',
                finishedAt: local.lastCompletedAt
            };
        }

        if (status.isRunning) {
            return {
                isRunning: true,
                lastStartedAt: status.lastStartedAt,
                lastCompletedAt: status.lastCompletedAt,
                lastError: status.lastError,
                processed: status.removed,
                total: status.currentlyMissing,
                percent: null,
                message: 'Catalog cleanup is running…',
                finishedAt: status.lastCompletedAt
            };
        }

        return {
            isRunning: false,
            lastStartedAt: status.lastStartedAt,
            lastCompletedAt: status.lastCompletedAt || local.lastCompletedAt,
            lastError: status.lastError || local.lastError,
            finishedAt: status.lastCompletedAt || local.lastCompletedAt,
            message: status.lastError || local.lastError || ''
        };
    }

    function describeAiGeneratePhase(job) {
        const phase = job.currentPhase || job.phase || '';
        const name = job.currentChannelName || job.channelName || '';
        const who = name || '…';
        const day = job.currentDay || 1;
        const totalDays = job.totalDays || 14;
        const page = job.page || 0;
        const totalPages = job.totalPages || 0;
        if (phase === 'priming' || String(phase).startsWith('priming')) {
            if (page) {
                return 'Priming ' + who + ' · page ' + page + '/' + (totalPages || '?');
            }
            return phase.indexOf('page') >= 0 ? who + ' · ' + phase : 'Priming catalog for ' + who;
        }
        if (phase === 'lineup' || phase === 'generating') {
            return 'Generating weekly lineup for ' + who;
        }
        if (phase === 'playout') {
            return 'Playout day ' + day + '/' + totalDays + ' · ' + who;
        }
        if (phase === 'horizon-full') {
            return who + ' already has a ' + totalDays + ' day guide';
        }
        if (phase === 'queuing' || phase === 'queued') {
            return name ? 'Queued ' + name : (job.message || 'Queued…');
        }
        if (phase === 'starting') {
            return 'Starting…';
        }
        if (job.message) {
            return job.message;
        }
        return phase ? who + ' · ' + phase : (name || 'Working…');
    }

    function mapAiGenerateTask(job) {
        if (!job) {
            return null;
        }

        const running = !!job.isRunning;
        if (!running && !job.completedAt && !job.startedAt && !job.phase && !job.currentPhase) {
            return null;
        }

        const totalSteps = job.totalSteps || job.totalDays || 0;
        const completedSteps = job.completedSteps != null ? job.completedSteps : (job.currentDay || 0);
        return {
            isRunning: running,
            lastStartedAt: job.startedAt,
            lastCompletedAt: job.completedAt,
            lastError: job.lastError || job.error,
            processed: completedSteps,
            total: totalSteps,
            percent: totalSteps
                ? Math.max(0, Math.min(100, Math.round(100 * (completedSteps || 0) / totalSteps)))
                : null,
            message: running
                ? describeAiGeneratePhase(job)
                : (job.lastError || job.error || job.message || (job.wasCancelled ? 'Cancelled.' : 'Finished.')),
            finishedAt: job.completedAt
        };
    }

    function refreshAiGeneratePopup(job) {
        const mapped = mapAiGenerateTask(job);
        if (!mapped) {
            return;
        }

        const snap = {
            kind: 'ai-generate',
            kicker: 'AI lineup generator',
            title: 'AI lineup generator',
            phase: mapped.isRunning ? 'running' : (mapped.lastError ? 'error' : 'done'),
            status: mapped
        };
        const snaps = readLibraryTaskSnaps().filter((row) => row.kind !== 'ai-generate');
        snaps.push(snap);
        writeLibraryTaskSnaps(snaps);
        renderLibraryTaskPopup(snap);
    }

    async function fetchTaskStatus(path) {
        try {
            return await api(path);
        } catch (ignore) {
            return null;
        }
    }

    async function listLibraryTaskSnaps() {
        const cached = readLibraryTaskSnaps();
        const cachedByKind = {};
        cached.forEach((snap) => {
            cachedByKind[snap.kind] = snap;
        });
        const [ffprobe, aspect, breaks, cleanupRaw, aiRaw] = await Promise.all([
            fetchTaskStatus('/tasks/ffprobe'),
            fetchTaskStatus('/tasks/true-aspect'),
            fetchTaskStatus('/tasks/commercial-breaks'),
            fetchTaskStatus('/tasks/catalog-cleanup'),
            fetchTaskStatus('/ai/generate-all/status')
        ]);
        const cleanup = cleanupRaw ? mapCleanupTask(cleanupRaw) : null;
        const aiGenerate = mapAiGenerateTask(aiRaw);
        const candidates = [
            { kind: 'true-aspect', kicker: 'True aspect ratio', title: 'True aspect ratio', status: aspect || cachedByKind['true-aspect']?.status || null },
            { kind: 'ffprobe', kicker: 'ffprobe chapters', title: 'ffprobe chapters', status: ffprobe || cachedByKind.ffprobe?.status || null },
            { kind: 'commercial-breaks', kicker: 'Commercial breaks', title: 'Commercial breaks', status: breaks || cachedByKind['commercial-breaks']?.status || null },
            { kind: 'cleanup', kicker: 'Catalog cleanup', title: 'Catalog cleanup', status: cleanup || cachedByKind.cleanup?.status || null },
            { kind: 'ai-generate', kicker: 'AI lineup generator', title: 'AI lineup generator', status: (aiGenerate && (aiGenerate.isRunning || aiGenerate.lastCompletedAt)) ? aiGenerate : (aiGenerate || cachedByKind['ai-generate']?.status || null) }
        ];

        return candidates.filter((row) => {
            if (!row.status || libraryTaskClosedKinds[row.kind]) {
                return false;
            }

            if (row.status.isRunning || libraryTaskStillPending(row.status, row.kind)) {
                if (!row.status.isRunning) {
                    row.status = Object.assign({}, row.status, {
                        isRunning: true,
                        message: row.status.message || cachedByKind[row.kind]?.status?.message || 'Starting…'
                    });
                }
                return true;
            }

            return thisLibraryTaskRunFinished(row.status, row.kind)
                && libraryTaskFinishedRecently(row.status)
                && (libraryTaskSeen[row.kind] || cachedByKind[row.kind]);
        });
    }

    function beginLibraryTaskPopup(kind, kicker, status) {
        libraryTaskKind = kind;
        delete libraryTaskClosedKinds[kind];
        libraryTaskSeen[kind] = true;
        libraryTaskExpecting[kind] = Date.now();
        if (libraryTaskHideTimers[kind]) {
            clearTimeout(libraryTaskHideTimers[kind]);
            delete libraryTaskHideTimers[kind];
        }

        const snap = {
            kind,
            kicker,
            title: kicker,
            phase: 'running',
            status: Object.assign({}, status || {}, {
                isRunning: true,
                lastStartedAt: new Date().toISOString(),
                message: (status && status.message) || 'Starting…'
            })
        };
        const snaps = readLibraryTaskSnaps().filter((row) => row.kind !== kind);
        snaps.push(snap);
        writeLibraryTaskSnaps(snaps);
        renderLibraryTaskPopup(snap);
        startCatalogSyncPolling();
    }

    function ensureLibraryTaskPopup(kind) {
        let el = taskPopupEl(kind);
        if (el) {
            return el;
        }

        const stack = taskPopupStackEl();
        if (!stack) {
            return null;
        }

        el = document.createElement('div');
        el.id = 'task-run-popup-' + kind;
        el.className = 'catalog-sync-popup task-run-popup';
        el.dataset.taskKind = kind;
        el.setAttribute('role', 'status');
        el.setAttribute('aria-live', 'polite');
        el.innerHTML = '<button type="button" class="catalog-sync-dismiss hidden" data-task-dismiss="'
            + kind + '" hidden aria-label="Dismiss">&times;</button>'
            + '<p class="catalog-sync-kicker task-run-kicker">Task</p>'
            + '<p class="catalog-sync-title task-run-title">Starting…</p>'
            + '<p class="catalog-sync-message task-run-message"></p>'
            + '<div class="catalog-sync-bar task-run-bar" data-indeterminate="true">'
            + '<div class="catalog-sync-bar-fill task-run-bar-fill"></div></div>'
            + '<p class="catalog-sync-meta task-run-meta"></p>';
        stack.appendChild(el);
        return el;
    }

    function renderLibraryTaskPopups(snaps) {
        const visible = {};
        (snaps || []).forEach((snap) => {
            if (snap && snap.kind) {
                visible[snap.kind] = true;
                renderLibraryTaskPopup(snap);
            }
        });
        const stack = taskPopupStackEl();
        if (stack) {
            stack.querySelectorAll('[data-task-kind]').forEach((el) => {
                if (!visible[el.dataset.taskKind]) {
                    el.remove();
                }
            });
        }

        writeLibraryTaskSnaps((snaps || []).map((snap) => ({
            kind: snap.kind,
            kicker: snap.kicker,
            title: snap.title,
            phase: snap.phase || ((snap.status && snap.status.isRunning) ? 'running' : 'done'),
            status: snap.status
        })));
    }

    function renderLibraryTaskPopup(picked) {
        if (!picked || !picked.kind) {
            return;
        }

        const kind = picked.kind;
        const el = ensureLibraryTaskPopup(kind);
        if (!el) {
            return;
        }

        const status = picked.status || {};
        const pending = libraryTaskStillPending(status, kind);
        const running = !!status.isRunning || pending;
        const runFinished = thisLibraryTaskRunFinished(status, kind);
        const phase = running ? 'running' : (status.lastError ? 'error' : (runFinished ? 'done' : 'running'));
        const finished = !running && (phase === 'done' || phase === 'error');
        const showFinished = finished && !libraryTaskClosedKinds[kind]
            && (libraryTaskSeen[kind] || libraryTaskFinishedRecently(status));
        if (libraryTaskWasRunning[kind] && !running && runFinished) {
            refreshCatalogAfterSync();
        }
        libraryTaskWasRunning[kind] = running;
        if (running) {
            libraryTaskSeen[kind] = true;
            delete libraryTaskClosedKinds[kind];
            if (libraryTaskHideTimers[kind]) {
                clearTimeout(libraryTaskHideTimers[kind]);
                delete libraryTaskHideTimers[kind];
            }
        }

        const kicker = el.querySelector('.task-run-kicker');
        const title = el.querySelector('.task-run-title');
        const message = el.querySelector('.task-run-message');
        const meta = el.querySelector('.task-run-meta');
        const bar = el.querySelector('.task-run-bar');
        const fill = el.querySelector('.task-run-bar-fill');
        const dismiss = el.querySelector('[data-task-dismiss]');

        if (running || showFinished) {
            el.hidden = false;
            el.classList.remove('hidden');
            el.classList.toggle('is-error', phase === 'error');
            el.classList.toggle('is-done', phase === 'done' && !running);
        } else {
            hideTaskRunPopup(kind);
            return;
        }

        if (kicker) {
            kicker.textContent = picked.kicker || 'Task';
        }
        if (title) {
            title.textContent = picked.title || 'Task';
        }
        if (message) {
            message.textContent = status.message || (running ? 'Starting…' : '');
        }
        if (meta) {
            meta.textContent = libraryTaskMeta(status);
        }
        if (bar && fill) {
            const indeterminate = running && (status.percent == null || status.percent === undefined);
            bar.dataset.indeterminate = indeterminate ? 'true' : 'false';
            fill.style.width = indeterminate ? '' : Math.max(0, Math.min(100, Number(status.percent) || 0)) + '%';
        }
        if (dismiss) {
            const canDismiss = !running && finished;
            dismiss.classList.toggle('hidden', !canDismiss);
            dismiss.hidden = !canDismiss;
        }

        if (showFinished && !running && !libraryTaskHideTimers[kind]) {
            libraryTaskHideTimers[kind] = setTimeout(() => {
                delete libraryTaskHideTimers[kind];
                delete libraryTaskSeen[kind];
                hideTaskRunPopup(kind);
            }, phase === 'error' ? 8000 : 4500);
        }
    }

    function bindTaskPopupStack() {
        const stack = taskPopupStackEl();
        if (!stack || taskPopupStackBound) {
            return;
        }

        taskPopupStackBound = true;
        stack.addEventListener('click', (event) => {
            const btn = event.target.closest('[data-task-dismiss]');
            if (!btn) {
                return;
            }

            const kind = btn.getAttribute('data-task-dismiss');
            if (kind === 'catalog-sync') {
                catalogSyncClosed = true;
                catalogSyncSeenRunning = false;
                if (catalogSyncHideTimer) {
                    clearTimeout(catalogSyncHideTimer);
                    catalogSyncHideTimer = null;
                }

                hideCatalogSyncPopup();
                return;
            }

            if (kind) {
                libraryTaskClosedKinds[kind] = true;
                hideTaskRunPopup(kind);
            }
        });
    }

    function readCatalogSyncSnap() {
        try {
            const raw = sessionStorage.getItem(CATALOG_SYNC_SNAP_KEY);
            return raw ? JSON.parse(raw) : null;
        } catch (ignore) {
            return null;
        }
    }

    function writeCatalogSyncSnap(snap) {
        try {
            if (!snap || (!snap.running && snap.phase !== 'done' && snap.phase !== 'error')) {
                sessionStorage.removeItem(CATALOG_SYNC_SNAP_KEY);
                return;
            }

            sessionStorage.setItem(CATALOG_SYNC_SNAP_KEY, JSON.stringify(snap));
        } catch (ignore) {
            // Private mode or quota — polling still works after load.
        }
    }

    function startCatalogSyncPolling() {
        bindTaskPopupStack();
        if (catalogSyncPollTimer) {
            return;
        }

        const cached = readCatalogSyncSnap();
        if (cached && (cached.running || catalogSyncFinishedRecently(cached))) {
            catalogSyncSeenRunning = true;
            catalogSyncClosed = false;
            renderCatalogSyncPopup(cached);
        }

        const libs = readLibraryTaskSnaps();
        libs.forEach((lib) => {
            if (lib && lib.status && (lib.status.isRunning || libraryTaskFinishedRecently(lib.status))) {
                libraryTaskSeen[lib.kind] = true;
                delete libraryTaskClosedKinds[lib.kind];
                renderLibraryTaskPopup(lib);
            }
        });

        void pollCatalogSyncProgress();
        catalogSyncPollTimer = setInterval(() => {
            void pollCatalogSyncProgress();
        }, 700);
    }

    async function pollCatalogSyncProgress() {
        try {
            renderCatalogSyncPopup(await api('/media-servers/sync/progress') || {});
        } catch (ignore) {
            // Keep the last popup state if the poll fails mid-sync.
        }

        try {
            const libraryTasks = await listLibraryTaskSnaps();
            renderLibraryTaskPopups(libraryTasks);
        } catch (ignore) {
            // Keep the last task popup if the poll fails mid-run.
        }
    }

    function catalogPopupActive(snap) {
        snap = snap || {};
        const running = !!snap.running;
        const phase = snap.phase || 'idle';
        const finished = phase === 'done' || phase === 'error';
        const showFinished = finished && !catalogSyncClosed && (catalogSyncSeenRunning || catalogSyncFinishedRecently(snap));
        const expecting = catalogSyncExpectingStart && (Date.now() - catalogSyncExpectingStart) < 8000;
        return running || showFinished || (phase === 'starting') || expecting;
    }

    function hideCatalogSyncPopup() {
        const el = $('catalog-sync-popup');
        if (!el) {
            return;
        }

        el.hidden = true;
        el.classList.add('hidden');
        el.classList.remove('is-done', 'is-error');
        writeCatalogSyncSnap(null);
    }

    function setCatalogSyncButtonsDisabled(disabled) {
        document.querySelectorAll('button[id^="ms-sync-"]').forEach((btn) => {
            if (/coming later/i.test(btn.textContent || '')) {
                return;
            }

            btn.disabled = !!disabled;
        });
    }

    function catalogSyncMeta(snap) {
        if (snap.metaText) {
            return snap.metaText;
        }

        const phase = snap.phase || '';
        if (phase === 'fetching') {
            let text = formatSyncCount(snap.items) + ' item' + (snap.items === 1 ? '' : 's');
            if (snap.libraryCount) {
                text += ' · library ' + (snap.libraryIndex || 1) + ' of ' + snap.libraryCount;
            }
            return text;
        }

        if (phase === 'saving') {
            return 'Saved ' + formatSyncCount(snap.saved) + ' of ' + formatSyncCount(snap.total || snap.items);
        }

        if (phase === 'chapters') {
            return 'Probed ' + formatSyncCount(snap.saved) + ' of ' + formatSyncCount(snap.total || snap.items);
        }

        if (phase === 'done') {
            return snap.percent != null ? snap.percent + '%' : '';
        }

        return '';
    }

    function refreshCatalogAfterSync() {
        loadMediaServers().then(() => {
            const kind = catalogSyncKind || libraryPage;
            if (kind && kind !== 'connections' && kind !== 'removed') {
                renderKindPanel(kind);
            }
        }).catch(() => {});
    }

    function renderCatalogSyncPopup(snap) {
        const el = $('catalog-sync-popup');
        if (!el) {
            return;
        }

        snap = snap || {};
        const running = !!snap.running;
        const phase = snap.phase || 'idle';
        const dismiss = $('catalog-sync-dismiss');
        const kicker = $('catalog-sync-kicker');
        const title = $('catalog-sync-title');
        const message = $('catalog-sync-message');
        const meta = $('catalog-sync-meta');
        const bar = $('catalog-sync-bar');
        const fill = $('catalog-sync-bar-fill');
        const finished = phase === 'done' || phase === 'error';
        const showFinished = finished && !catalogSyncClosed && (catalogSyncSeenRunning || catalogSyncFinishedRecently(snap));
        const persistCatalog = !snap.kicker || snap.kicker === 'Catalog sync' || snap.kicker === 'Library scan';

        if (catalogSyncWasRunning && !running) {
            refreshCatalogAfterSync();
        }
        catalogSyncWasRunning = running;

        if (running) {
            catalogSyncSeenRunning = true;
            catalogSyncClosed = false;
            if (phase && phase !== 'starting') {
                catalogSyncExpectingStart = 0;
            }
            if (catalogSyncHideTimer) {
                clearTimeout(catalogSyncHideTimer);
                catalogSyncHideTimer = null;
            }

            el.hidden = false;
            el.classList.remove('hidden', 'is-done', 'is-error');
            setCatalogSyncButtonsDisabled(true);
            if (persistCatalog) {
                writeCatalogSyncSnap(snap);
            }
        } else if (showFinished) {
            catalogSyncSeenRunning = true;
            el.hidden = false;
            el.classList.remove('hidden');
            el.classList.toggle('is-error', phase === 'error');
            el.classList.toggle('is-done', phase === 'done');
            setCatalogSyncButtonsDisabled(false);
            if (persistCatalog) {
                writeCatalogSyncSnap(snap);
            }
            if (!catalogSyncHideTimer) {
                catalogSyncHideTimer = setTimeout(() => {
                    catalogSyncHideTimer = null;
                    catalogSyncSeenRunning = false;
                    hideCatalogSyncPopup();
                }, phase === 'error' ? 8000 : 4500);
            }
        } else if (!running && !showFinished) {
            setCatalogSyncButtonsDisabled(false);
            const expecting = catalogSyncExpectingStart && (Date.now() - catalogSyncExpectingStart) < 8000;
            if (phase === 'idle' && !expecting && catalogSyncSeenRunning) {
                catalogSyncSeenRunning = false;
                hideCatalogSyncPopup();
            }
        }

        if (kicker) {
            kicker.textContent = snap.kicker || 'Catalog sync';
        }

        if (title) {
            title.textContent = snap.serverName || 'Catalog sync';
        }

        if (message) {
            message.textContent = snap.message || '';
        }

        if (meta) {
            meta.textContent = catalogSyncMeta(snap);
        }

        if (bar && fill) {
            const indeterminate = running && (snap.percent == null || snap.percent === undefined);
            bar.dataset.indeterminate = indeterminate ? 'true' : 'false';
            fill.style.width = indeterminate ? '' : Math.max(0, Math.min(100, Number(snap.percent) || 0)) + '%';
        }

        if (dismiss) {
            const canDismiss = !running && finished;
            dismiss.classList.toggle('hidden', !canDismiss);
            dismiss.hidden = !canDismiss;
        }
    }

    async function syncServerCatalog(id, kind) {
        catalogSyncKind = kind;
        catalogSyncClosed = false;
        catalogSyncSeenRunning = true;
        catalogSyncExpectingStart = Date.now();
        if (catalogSyncHideTimer) {
            clearTimeout(catalogSyncHideTimer);
            catalogSyncHideTimer = null;
        }

        renderCatalogSyncPopup({
            running: true,
            phase: 'starting',
            serverName: '',
            message: 'Starting catalog sync…',
            percent: null,
            items: 0,
            saved: 0,
            total: 0
        });
        startCatalogSyncPolling();
        try {
            await api('/media-servers/' + id + '/sync', { method: 'POST' });
            await pollCatalogSyncProgress();
        } catch (err) {
            await pollCatalogSyncProgress().catch(() => {});
            if (/already running/i.test(err.message || '')) {
                toast('A catalog sync is already running.', 'info');
            } else {
                toast(err.message, 'error');
            }
        }
    }

    async function loadKindCatalog(kind, connectionId) {
        try {
            const data = await api('/media-servers/catalog?connectionId=' + encodeURIComponent(connectionId)) || {};
            const totals = data.totals || {};
            renderCatalogTvTable('ms-media-' + kind + '-tv', data.tvShows, totals.tvShows, connectionId, kind);
            renderCatalogMovieList('ms-media-' + kind + '-movies', data.movies, totals.movies);
            renderCatalogArtistTable('ms-media-' + kind + '-music', data.music, totals.music, connectionId, kind, {
                endpoint: '/media-servers/catalog/music',
                nounSingular: 'song',
                nounPlural: 'songs',
                groupLabel: 'artists',
                emptyItems: 'No songs synced for this artist.'
            });
            renderCatalogArtistTable('ms-media-' + kind + '-musicvideos', data.musicVideos, totals.musicVideos, connectionId, kind, {
                endpoint: '/media-servers/catalog/musicvideos',
                nounSingular: 'music video',
                nounPlural: 'music videos',
                groupLabel: 'stars',
                emptyItems: 'No music videos synced for this star.'
            });
            renderCatalogMediaTable('ms-media-' + kind + '-news', data.pastTenseNews, totals.pastTenseNews);
        } catch (err) {
            reportApiError(err, 'Could not load catalog.');
        }
    }

    async function loadLibraryTab() {
        try {
            toggleMediaServerAddFields();
            await loadMediaServers();
            applyLibraryPage();
        } catch (err) {
            reportApiError(err, 'Could not load media servers.');
        }
    }

    async function addMediaServer() {
        const kind = $('ms-kind').value;
        const status = $('ms-add-status');
        status.textContent = '';
        try {
            await api('/media-servers', {
                method: 'POST',
                body: JSON.stringify({
                    kind,
                    name: $('ms-name').value,
                    baseUrl: $('ms-url').value,
                    accessToken: $('ms-token').value,
                    sidecarRoot: $('ms-root').value
                })
            });
            $('ms-name').value = '';
            $('ms-token').value = '';
            toast('Server added. Use Test server, then refresh libraries.', 'success');
            await loadMediaServers();
        } catch (err) {
            status.textContent = err.message;
        }
    }

    function toggleMediaServerAddFields() {
        const sidecar = $('ms-kind').value === 'sidecar';
        $('ms-url-field').classList.toggle('hidden', sidecar);
        $('ms-token-field').classList.toggle('hidden', sidecar);
        $('ms-root-field').classList.toggle('hidden', !sidecar);
    }

    async function loadLibraryRemoved() {
        try {
            await loadCatalogCleanup();
            const grace = $('catalog-cleanup-grace');
            const localGrace = $('library-cleanup-grace');
            if (grace && localGrace) {
                localGrace.value = grace.value;
            }
            $('library-cleanup-status').textContent = $('catalog-cleanup-status')?.textContent || '';
            $('library-scan-status').textContent = $('catalog-local-scan-status')?.textContent || '';
            const data = await api('/media-servers/removed') || {};
            const el = $('library-removed-table');
            const items = data.items || [];
            if (!items.length) {
                el.innerHTML = '<div class="empty-state">Nothing is marked missing.</div>';
                return;
            }
            el.innerHTML = `<table class="data-table"><thead><tr><th>Title</th><th>Server</th><th>Library</th><th>Path</th><th>Missing since</th></tr></thead><tbody>${
                items.map((row) => `<tr>
                    <td>${escapeHtml(row.name || '')}</td>
                    <td>${escapeHtml(row.serverName || '')}</td>
                    <td>${escapeHtml(row.libraryName || '')}</td>
                    <td>${escapeHtml(row.path || '')}</td>
                    <td>${escapeHtml(row.missingSince || '')}</td>
                </tr>`).join('')
            }</tbody></table>`;
        } catch (err) {
            reportApiError(err, 'Could not load removed items.');
        }
    }

    async function loadJellyfinLibraries() {
        await loadLibraryTab();
    }

    async function saveJellyfinLibraries() {
        await loadLibraryTab();
    }

    function catalogTrueAspectLabel(value) {
        const text = String(value || '').trim();
        if (!text) {
            return '';
        }

        if (/^16:9$/i.test(text)) {
            return '16:9';
        }

        if (/^4:3$/i.test(text)) {
            return '4:3';
        }

        const parts = text.replace(/[x×\/]/gi, ':').split(':');
        if (parts.length === 2) {
            const width = Number(parts[0]);
            const height = Number(parts[1]);
            if (width > 0 && height > 0) {
                const ratio = width / height;
                if (Math.abs(ratio - 16 / 9) / (16 / 9) <= 0.02) {
                    return '16:9';
                }

                if (Math.abs(ratio - 4 / 3) / (4 / 3) <= 0.02) {
                    return '4:3';
                }

                return (Math.round(ratio * 100) / 100).toString() + ':1';
            }
        }

        const asNumber = Number(text);
        if (asNumber > 0) {
            return (Math.round(asNumber * 100) / 100).toString() + ':1';
        }

        return text;
    }

    function catalogFormat(row) {
        const measured = String((row && (row.trueAspectRatio || row.TrueAspectRatio)) || '').trim();
        if (measured) {
            return catalogTrueAspectLabel(measured);
        }

        return String((row && row.format) || '').trim();
    }

    function renderCatalogTvTable(containerId, rows, total, connectionId, kind) {
        const el = $(containerId);
        if (!el) {
            return;
        }

        const items = Array.isArray(rows) ? rows : [];
        if (!items.length) {
            el.innerHTML = '<div class="empty-state">No synced items yet. Add a connection, refresh libraries, then sync.</div>';
            return;
        }

        const count = typeof total === 'number' ? total : items.length;
        const note = count > items.length
            ? `<div class="meta">Showing ${items.length} of ${count} shows</div>`
            : '';

        el.innerHTML = `${note}<div class="catalog-show-list">${items.map((row) => {
            const episodeCount = Number(row.episodeCount) || 0;
            const episodeLabel = episodeCount === 1 ? '1 episode' : `${episodeCount} episodes`;
            const rating = row.rating || row.officialRating || '';
            const meta = [episodeLabel, rating, row.libraryName].filter(Boolean).join(' · ');
            return `<details class="catalog-show" data-series-id="${escapeHtml(row.seriesId || row.id || '')}" data-series-name="${escapeHtml(row.seriesName || row.name || '')}">
                <summary>
                    <span class="catalog-show-title">${escapeHtml(row.name || 'Untitled series')}</span>
                    <span class="catalog-show-meta">${escapeHtml(meta)}</span>
                </summary>
                <div class="catalog-show-body">
                    ${row.plot ? `<p class="catalog-show-plot">${escapeHtml(row.plot)}</p>` : ''}
                    <div class="catalog-show-episodes"><div class="hint">Click to load episodes…</div></div>
                </div>
            </details>`;
        }).join('')}</div>`;

        el.querySelectorAll('details.catalog-show').forEach((details) => {
            details.addEventListener('toggle', () => {
                if (!details.open || details.dataset.loaded === 'true' || details.dataset.loading === 'true') {
                    return;
                }

                loadCatalogShowEpisodes(details, connectionId, kind).catch((err) => {
                    const box = details.querySelector('.catalog-show-episodes');
                    if (box) {
                        box.innerHTML = `<div class="empty-state">${escapeHtml(err.message || 'Could not load episodes.')}</div>`;
                    }
                });
            });
        });
    }

    async function loadCatalogShowEpisodes(details, connectionId, kind) {
        const box = details.querySelector('.catalog-show-episodes');
        if (!box) {
            return;
        }

        details.dataset.loading = 'true';
        box.innerHTML = '<div class="hint">Loading episodes…</div>';
        const params = new URLSearchParams();
        if (connectionId) {
            params.set('connectionId', connectionId);
        }
        if (kind) {
            params.set('kind', kind);
        }

        const seriesId = details.dataset.seriesId || '';
        const seriesName = details.dataset.seriesName || '';
        if (seriesId && seriesId !== '00000000-0000-0000-0000-000000000000') {
            params.set('seriesId', seriesId);
        } else if (seriesName) {
            params.set('seriesName', seriesName);
        }

        try {
            const data = await api('/media-servers/catalog/episodes?' + params.toString()) || {};
            const episodes = Array.isArray(data.episodes) ? data.episodes : [];
            details.dataset.loaded = 'true';
            box.innerHTML = renderCatalogEpisodeRows(episodes);
        } finally {
            delete details.dataset.loading;
        }
    }

    function catalogSeasonKey(row) {
        if (row.season != null && row.season !== '') {
            return 'n:' + String(row.season);
        }

        const name = (row.seasonName || '').trim();
        if (name) {
            return 's:' + name.toLowerCase();
        }

        return 'unknown';
    }

    function catalogSeasonLabel(row) {
        if (row.season != null && row.season !== '') {
            return 'Season ' + String(row.season).padStart(2, '0');
        }

        const name = (row.seasonName || '').trim();
        if (name) {
            return /^season\b/i.test(name) ? name : 'Season ' + name;
        }

        return 'Season unknown';
    }

    function catalogSeasonSortValue(row) {
        if (row.season != null && row.season !== '') {
            const n = Number(row.season);
            return Number.isFinite(n) ? n : 9999;
        }

        return 10000;
    }

    function groupCatalogEpisodesBySeason(episodes) {
        const groups = [];
        const byKey = new Map();
        episodes.forEach((row) => {
            const key = catalogSeasonKey(row);
            let group = byKey.get(key);
            if (!group) {
                group = {
                    key,
                    label: catalogSeasonLabel(row),
                    sort: catalogSeasonSortValue(row),
                    episodes: []
                };
                byKey.set(key, group);
                groups.push(group);
            }
            group.episodes.push(row);
        });

        groups.sort((a, b) => a.sort - b.sort || a.label.localeCompare(b.label, undefined, { numeric: true, sensitivity: 'base' }));
        groups.forEach((group) => {
            group.episodes.sort((a, b) => {
                const ea = Number(a.episode);
                const eb = Number(b.episode);
                if (Number.isFinite(ea) && Number.isFinite(eb) && ea !== eb) {
                    return ea - eb;
                }

                return String(a.name || '').localeCompare(String(b.name || ''), undefined, { numeric: true, sensitivity: 'base' });
            });
        });
        return groups;
    }

    function renderCatalogEpisodeRows(episodes) {
        if (!episodes.length) {
            return '<div class="empty-state">No episodes synced for this show.</div>';
        }

        const seasons = groupCatalogEpisodesBySeason(episodes);
        return `<div class="catalog-season-list">${seasons.map((season) => {
            const episodeLabel = season.episodes.length === 1 ? '1 episode' : `${season.episodes.length} episodes`;
            const rows = season.episodes.map((row) => `<tr>
                <td>${escapeHtml(row.code || '')}</td>
                <td>${escapeHtml(row.name || '')}</td>
                <td>${escapeHtml(row.runtime || '')}</td>
                <td>${escapeHtml(catalogFormat(row))}</td>
                <td>${escapeHtml(String(row.chapters ?? row.chapterCount ?? ''))}</td>
                <td>${escapeHtml(row.rating || '')}</td>
                <td class="catalog-plot" title="${escapeHtml(row.plot || '')}">${escapeHtml(row.plot || '')}</td>
                <td class="catalog-path" title="${escapeHtml(row.path || '')}">${escapeHtml(row.path || '')}</td>
            </tr>`).join('');
            return `<details class="catalog-season">
                <summary>
                    <span class="catalog-show-title">${escapeHtml(season.label)}</span>
                    <span class="catalog-show-meta">${escapeHtml(episodeLabel)}</span>
                </summary>
                <div class="catalog-season-body">
                    <table class="data-table catalog-episode-table">
                        <thead>
                            <tr>
                                <th>Episode</th>
                                <th>Title</th>
                                <th>Runtime</th>
                                <th>Format</th>
                                <th>Chapters</th>
                                <th>Rating</th>
                                <th>Plot</th>
                                <th>Path</th>
                            </tr>
                        </thead>
                        <tbody>${rows}</tbody>
                    </table>
                </div>
            </details>`;
        }).join('')}</div>`;
    }

    function renderCatalogMovieList(containerId, rows, total) {
        const el = $(containerId);
        if (!el) {
            return;
        }

        const items = Array.isArray(rows) ? rows : [];
        if (!items.length) {
            el.innerHTML = '<div class="empty-state">No synced items yet. Add a connection, refresh libraries, then sync.</div>';
            return;
        }

        const count = typeof total === 'number' ? total : items.length;
        const note = count > items.length
            ? `<div class="meta">Showing ${items.length} of ${count} movies</div>`
            : '';

        el.innerHTML = `${note}<div class="catalog-show-list catalog-movie-list">${items.map((row) => {
            const chapters = row.chapters || row.chapterCount;
            const chapterLabel = chapters
                ? (Number(chapters) === 1 ? '1 chapter' : `${chapters} chapters`)
                : '';
            const meta = [
                row.runtime,
                catalogFormat(row),
                chapterLabel,
                row.year || row.productionYear,
                row.rating || row.officialRating,
                row.libraryName
            ].filter(Boolean).join(' · ');
            const plot = row.plot || row.overview || '';
            const stars = row.stars || '';
            const path = row.path || '';
            return `<div class="catalog-show catalog-movie">
                <div class="catalog-movie-row">
                    <span class="catalog-show-title">${escapeHtml(row.name || 'Untitled')}</span>
                    <span class="catalog-show-meta">${escapeHtml(meta)}</span>
                </div>
                <div class="catalog-show-body">
                    ${plot ? `<p class="catalog-show-plot">${escapeHtml(plot)}</p>` : ''}
                    ${stars ? `<p class="catalog-show-meta">${escapeHtml(stars)}</p>` : ''}
                    ${path ? `<p class="catalog-path" title="${escapeHtml(path)}">${escapeHtml(path)}</p>` : ''}
                </div>
            </div>`;
        }).join('')}</div>`;
    }

    function renderCatalogArtistTable(containerId, rows, total, connectionId, kind, options) {
        const el = $(containerId);
        if (!el) {
            return;
        }

        options = options || {};
        const items = Array.isArray(rows) ? rows : [];
        if (!items.length) {
            el.innerHTML = '<div class="empty-state">No synced items yet. Add a connection, refresh libraries, then sync.</div>';
            return;
        }

        const count = typeof total === 'number' ? total : items.length;
        const groupLabel = options.groupLabel || 'artists';
        const note = count > items.length
            ? `<div class="meta">Showing ${items.length} of ${count} ${groupLabel}</div>`
            : '';
        const nounSingular = options.nounSingular || 'item';
        const nounPlural = options.nounPlural || 'items';

        el.innerHTML = `${note}<div class="catalog-show-list">${items.map((row) => {
            const itemCount = Number(row.itemCount) || 0;
            const itemLabel = itemCount === 1 ? '1 ' + nounSingular : `${itemCount} ${nounPlural}`;
            const artistName = row.artistName || row.name || 'Unknown artist';
            return `<details class="catalog-show" data-artist-name="${escapeHtml(artistName)}">
                <summary>
                    <span class="catalog-show-title">${escapeHtml(artistName)}</span>
                    <span class="catalog-show-meta">${escapeHtml(itemLabel)}</span>
                </summary>
                <div class="catalog-show-body">
                    <div class="catalog-show-episodes"><div class="hint">Click to load ${nounPlural}…</div></div>
                </div>
            </details>`;
        }).join('')}</div>`;

        el.querySelectorAll('details.catalog-show').forEach((details) => {
            details.addEventListener('toggle', () => {
                if (!details.open || details.dataset.loaded === 'true' || details.dataset.loading === 'true') {
                    return;
                }

                loadCatalogArtistItems(details, connectionId, kind, options).catch((err) => {
                    const box = details.querySelector('.catalog-show-episodes');
                    if (box) {
                        box.innerHTML = `<div class="empty-state">${escapeHtml(err.message || 'Could not load items.')}</div>`;
                    }
                });
            });
        });
    }

    async function loadCatalogArtistItems(details, connectionId, kind, options) {
        const box = details.querySelector('.catalog-show-episodes');
        if (!box) {
            return;
        }

        details.dataset.loading = 'true';
        box.innerHTML = '<div class="hint">Loading…</div>';
        const params = new URLSearchParams();
        if (connectionId) {
            params.set('connectionId', connectionId);
        }
        if (kind) {
            params.set('kind', kind);
        }

        const artistName = details.dataset.artistName || '';
        if (artistName) {
            params.set('artist', artistName);
        }

        try {
            const data = await api((options.endpoint || '') + '?' + params.toString()) || {};
            const items = Array.isArray(data.items) ? data.items : [];
            details.dataset.loaded = 'true';
            box.innerHTML = renderCatalogArtistItemRows(items, options.emptyItems || 'Nothing synced for this artist.');
        } finally {
            delete details.dataset.loading;
        }
    }

    function renderCatalogArtistItemRows(items, emptyMessage) {
        if (!items.length) {
            return `<div class="empty-state">${escapeHtml(emptyMessage)}</div>`;
        }

        const showAlbum = items.some((row) => row.album);
        return `<div class="catalog-season-body"><table class="data-table catalog-episode-table">
            <thead>
                <tr>
                    <th>Title</th>
                    ${showAlbum ? '<th>Album</th>' : ''}
                    <th>Runtime</th>
                    <th>Format</th>
                    <th>Chapters</th>
                    <th>Rating</th>
                    <th>Plot</th>
                    <th>Stars</th>
                    <th>Path</th>
                </tr>
            </thead>
            <tbody>
                ${items.map((row) => `<tr>
                    <td>${escapeHtml(row.name || '')}</td>
                    ${showAlbum ? `<td>${escapeHtml(row.album || '')}</td>` : ''}
                    <td>${escapeHtml(row.runtime || '')}</td>
                    <td>${escapeHtml(catalogFormat(row))}</td>
                    <td>${escapeHtml(String(row.chapters ?? row.chapterCount ?? ''))}</td>
                    <td>${escapeHtml(row.rating || '')}</td>
                    <td class="catalog-plot" title="${escapeHtml(row.plot || '')}">${escapeHtml(row.plot || '')}</td>
                    <td>${escapeHtml(row.stars || '')}</td>
                    <td class="catalog-path" title="${escapeHtml(row.path || '')}">${escapeHtml(row.path || '')}</td>
                </tr>`).join('')}
            </tbody>
        </table></div>`;
    }

    function renderCatalogMediaTable(containerId, rows, total) {
        const el = $(containerId);
        if (!el) {
            return;
        }

        const items = Array.isArray(rows) ? rows : [];
        if (!items.length) {
            el.innerHTML = '<div class="empty-state">No synced items yet. Add a connection, refresh libraries, then sync.</div>';
            return;
        }

        const count = typeof total === 'number' ? total : items.length;
        const note = count > items.length
            ? `<div class="meta">Showing ${items.length} of ${count}</div>`
            : '';

        el.innerHTML = `${note}<table class="data-table">
            <thead>
                <tr>
                    <th>Title</th>
                    <th>Runtime</th>
                    <th>Format</th>
                    <th>Chapters</th>
                    <th>Rating</th>
                    <th>Plot</th>
                    <th>Stars</th>
                    <th>Jellyfin path</th>
                    <th>IDs</th>
                </tr>
            </thead>
            <tbody>
                ${items.map((row) => `<tr>
                    <td>${escapeHtml(row.name || '')}</td>
                    <td>${escapeHtml(row.runtime || '')}</td>
                    <td>${escapeHtml(catalogFormat(row))}</td>
                    <td>${escapeHtml(String(row.chapters ?? row.chapterCount ?? ''))}</td>
                    <td>${escapeHtml(row.rating || row.officialRating || '')}</td>
                    <td class="catalog-plot" title="${escapeHtml(row.plot || row.overview || '')}">${escapeHtml(row.plot || row.overview || '')}</td>
                    <td>${escapeHtml(row.stars || '')}</td>
                    <td class="catalog-path" title="${escapeHtml(row.path || '')}">${escapeHtml(row.path || '')}</td>
                    <td class="catalog-ids" title="${escapeHtml(row.ids || '')}">${escapeHtml(row.ids || '')}</td>
                </tr>`).join('')}
            </tbody>
        </table>`;
    }

    function renderListsTable() {
        const el = $('lists-table');
        if (!finTvLists.length) {
            el.innerHTML = '<div class="empty-state">No ChannelFlow lists registered yet. Add a Jellyfin playlist to use it in lineups.</div>';
            return;
        }

        el.innerHTML = finTvLists.map((list) => {
            const mode = list.playbackMode === 1 ? 'Random' : 'Sequential';
            return `<div class="list-card">
                <div>
                    <strong>${escapeHtml(list.name)}</strong>
                    <div class="meta">${list.itemCount || 0} items · ${mode}</div>
                </div>
                <div class="row-actions">
                    <button type="button" data-edit-list="${list.id}">Edit</button>
                    <button type="button" data-delete-list="${list.id}">Delete</button>
                </div>
            </div>`;
        }).join('');

        el.querySelectorAll('[data-edit-list]').forEach((btn) => {
            btn.onclick = () => openListForm(btn.dataset.editList);
        });
        el.querySelectorAll('[data-delete-list]').forEach((btn) => {
            btn.onclick = () => deleteList(btn.dataset.deleteList);
        });
    }

    async function openListForm(editId) {
        const existing = editId ? finTvLists.find((l) => l.id === editId) : null;
        let jellyfinOptions = '';

        if (!existing) {
            const playlists = await api('/lists/jellyfin-playlists?unregisteredOnly=true') || [];
            if (!playlists.length) {
                toast('No unregistered Jellyfin playlists found.', 'info');
                return;
            }

            jellyfinOptions = playlists.map((p) =>
                `<option value="${p.id}">${escapeHtml(p.name)} (${p.itemCount} items)</option>`).join('');
        }

        const body = existing
            ? `<label class="field"><span>Name</span><input id="list-name" class="emby-input" value="${escapeHtml(existing.name)}"></label>
               <label class="field"><span>Playback mode</span>
                 <select id="list-mode" class="emby-select">
                   <option value="0"${existing.playbackMode === 0 ? ' selected' : ''}>Sequential</option>
                   <option value="1"${existing.playbackMode === 1 ? ' selected' : ''}>Random</option>
                 </select></label>`
            : `<label class="field"><span>Jellyfin playlist</span>
                 <select id="list-jellyfin-id" class="emby-select">${jellyfinOptions}</select></label>
               <label class="field"><span>Display name (optional)</span><input id="list-name" class="emby-input"></label>
               <label class="field"><span>Playback mode</span>
                 <select id="list-mode" class="emby-select">
                   <option value="0">Sequential</option>
                   <option value="1">Random</option>
                 </select></label>`;

        openModal(existing ? 'Edit ChannelFlow List' : 'Add ChannelFlow List', body, `
            <button type="button" class="emby-button" id="list-cancel">Cancel</button>
            <button type="button" class="raised button-submit emby-button" id="list-save">Save</button>`);

        document.getElementById('list-cancel').onclick = closeModal;
        document.getElementById('list-save').onclick = async () => {
            try {
                if (existing) {
                    await api('/lists/' + existing.id, {
                        method: 'PUT',
                        body: JSON.stringify({
                            name: document.getElementById('list-name').value.trim(),
                            playbackMode: parseInt(document.getElementById('list-mode').value, 10)
                        })
                    });
                } else {
                    await api('/lists', {
                        method: 'POST',
                        body: JSON.stringify({
                            jellyfinPlaylistId: document.getElementById('list-jellyfin-id').value,
                            name: document.getElementById('list-name').value.trim(),
                            playbackMode: parseInt(document.getElementById('list-mode').value, 10)
                        })
                    });
                }

                closeModal();
                toast('List saved.', 'success');
                await loadLists();
            } catch (err) {
                reportApiError(err, 'Could not save list.');
            }
        };
    }

    async function deleteList(id) {
        if (!confirm('Remove this ChannelFlow list registration?')) return;
        try {
            await api('/lists/' + id, { method: 'DELETE' });
            toast('List removed.', 'success');
            await loadLists();
        } catch (err) {
            reportApiError(err, 'Could not delete list.');
        }
    }

    function populateSpecialChannelSelect() {
        const select = $('special-channel-select');
        if (!select) return;
        select.innerHTML = channels
            .filter((c) => c.contentType !== CONTENT_TYPE_VALUES.Weather)
            .map((c) => `<option value="${c.id}">${escapeHtml(formatChannelNumber(c.number) + ' · ' + c.name)}</option>`)
            .join('');
        if (!specialChannelId && select.options.length) {
            specialChannelId = select.value;
        } else if (specialChannelId) {
            select.value = specialChannelId;
        }
    }

    async function loadSpecialPresentations() {
        populateSpecialChannelSelect();
        specialChannelId = $('special-channel-select').value;
        if (!specialChannelId) {
            $('special-list').innerHTML = '<div class="empty-state">Create a non-weather channel first.</div>';
            return;
        }

        try {
            await ensureFinTvLists();
            specialPresentations = await api('/special-presentations/' + specialChannelId) || [];
            renderSpecialPresentationList();
        } catch (err) {
            reportApiError(err, 'Could not load special presentations.');
        }
    }

    function presentationSummary(p) {
        const candidates = p.candidates || [];
        if (!candidates.length) return 'No content';
        if (candidates.length === 1) return candidateSummary(candidates[0]);
        return `${candidates.length} candidates`;
    }

    function renderSpecialPresentationList() {
        const el = $('special-list');
        if (!specialPresentations.length) {
            el.innerHTML = '<div class="empty-state">No special presentations configured for this channel.</div>';
            return;
        }

        el.innerHTML = specialPresentations.map((p) => {
            const span = Math.max(1, p.spanSlots || 1);
            return `<div class="presentation-card">
                <div>
                    <strong>${escapeHtml(p.name)}</strong>${p.enabled ? '' : ' <span class="meta">(disabled)</span>'}
                    <div class="meta">${DAYS[p.dayOfWeek]} · ${slotTimeInputValue(p.slotIndex)} · ${span * 30} min · ${escapeHtml(presentationSummary(p))}</div>
                </div>
                <div class="row-actions">
                    <button type="button" data-edit-special="${p.id}">Edit</button>
                    <button type="button" data-delete-special="${p.id}">Delete</button>
                </div>
            </div>`;
        }).join('');

        el.querySelectorAll('[data-edit-special]').forEach((btn) => {
            btn.onclick = () => openSpecialPresentationForm(btn.dataset.editSpecial);
        });
        el.querySelectorAll('[data-delete-special]').forEach((btn) => {
            btn.onclick = () => deleteSpecialPresentation(btn.dataset.deleteSpecial);
        });
    }

    function buildRuleFilterJson() {
        const tags = (document.getElementById('sp-tags')?.value || '')
            .split(',')
            .map((t) => t.trim())
            .filter(Boolean);
        const filter = {};
        const genre = document.getElementById('sp-genre')?.value.trim();
        const titleContains = document.getElementById('sp-title')?.value.trim();
        const minYear = document.getElementById('sp-min-year')?.value;
        const maxYear = document.getElementById('sp-max-year')?.value;
        const minRating = document.getElementById('sp-min-rating')?.value.trim();
        const maxRating = document.getElementById('sp-max-rating')?.value.trim();
        if (genre) filter.genre = genre;
        if (tags.length) filter.tags = tags;
        if (titleContains) filter.titleContains = titleContains;
        if (minYear) filter.minYear = parseInt(minYear, 10);
        if (maxYear) filter.maxYear = parseInt(maxYear, 10);
        if (minRating) filter.minRating = minRating;
        if (maxRating) filter.maxRating = maxRating;
        return JSON.stringify(filter);
    }

    async function openSpecialPresentationForm(editId) {
        const existing = editId ? specialPresentations.find((p) => p.id === editId) : null;
        await ensureFinTvLists();
        const channel = channels.find((c) => c.id === specialChannelId);
        const draft = existing
            ? JSON.parse(JSON.stringify(existing))
            : { name: '', enabled: true, dayOfWeek: 1, slotIndex: 36, spanSlots: 2, candidates: [] };

        let contentMode = 0;
        if (draft.candidates?.length === 1) {
            if (draft.candidates[0].kind === 2) contentMode = 1;
            if (draft.candidates[0].kind === 3) contentMode = 2;
        }

        const body = `
            <label class="field"><span>Name</span><input id="sp-name" class="emby-input" value="${escapeHtml(draft.name || '')}"></label>
            <label class="field checkbox-field"><input id="sp-enabled" type="checkbox"${draft.enabled !== false ? ' checked' : ''}><span class="channelflow-check-box" aria-hidden="true"></span><span>Enabled</span></label>
            <label class="field"><span>Day of week</span>
                <select id="sp-day" class="emby-select">${DAYS.map((d, i) =>
                    `<option value="${i}"${draft.dayOfWeek === i ? ' selected' : ''}>${d}</option>`).join('')}</select></label>
            <label class="field"><span>Start time</span><input id="sp-time" type="time" class="emby-input" value="${slotTimeInputValue(draft.slotIndex || 0)}"></label>
            <label class="field"><span>Block length (30-min slots)</span><input id="sp-span" type="number" min="1" max="8" class="emby-input" value="${Math.max(1, draft.spanSlots || 1)}"></label>
            <label class="field"><span>Content mode</span>
                <select id="sp-content-mode" class="emby-select">
                    <option value="0">Fixed items</option>
                    <option value="1">Rule-based</option>
                    <option value="2">ChannelFlow list</option>
                </select></label>
            <div id="sp-content-panel"></div>`;

        openModal(existing ? 'Edit Special Presentation' : 'Add Special Presentation', body, `
            <button type="button" class="emby-button" id="sp-cancel">Cancel</button>
            <button type="button" class="raised button-submit emby-button" id="sp-save">Save</button>`);

        document.getElementById('sp-content-mode').value = String(contentMode);
        draft.candidates = draft.candidates || [];

        function renderContentPanel() {
            const mode = parseInt(document.getElementById('sp-content-mode').value, 10);
            const panel = document.getElementById('sp-content-panel');
            if (mode === 1) {
                let filter = {};
                try { filter = JSON.parse(draft.candidates[0]?.filterJson || '{}'); } catch (e) { filter = {}; }
                panel.innerHTML = `
                    <label class="field"><span>Genre</span><input id="sp-genre" class="emby-input" value="${escapeHtml(filter.genre || '')}"></label>
                    <label class="field"><span>Tags (comma-separated)</span><input id="sp-tags" class="emby-input" value="${escapeHtml((filter.tags || []).join(', '))}"></label>
                    <label class="field"><span>Title contains</span><input id="sp-title" class="emby-input" value="${escapeHtml(filter.titleContains || '')}"></label>
                    <div class="form-grid">
                        <label class="field"><span>Min year</span><input id="sp-min-year" type="number" class="emby-input" value="${filter.minYear || ''}"></label>
                        <label class="field"><span>Max year</span><input id="sp-max-year" type="number" class="emby-input" value="${filter.maxYear || ''}"></label>
                    </div>
                    <div class="form-grid">
                        <label class="field"><span>Min rating</span><input id="sp-min-rating" class="emby-input" placeholder="PG" value="${escapeHtml(filter.minRating || '')}"></label>
                        <label class="field"><span>Max rating</span><input id="sp-max-rating" class="emby-input" placeholder="PG-13" value="${escapeHtml(filter.maxRating || '')}"></label>
                    </div>`;
                return;
            }

            if (mode === 2) {
                panel.innerHTML = `<label class="field"><span>ChannelFlow list</span>
                    <select id="sp-list-id" class="emby-select">
                        ${finTvLists.map((l) => `<option value="${l.id}"${draft.candidates[0]?.finTvListId === l.id ? ' selected' : ''}>${escapeHtml(l.name)}</option>`).join('')}
                    </select></label>`;
                return;
            }

            panel.innerHTML = `
                <div id="sp-candidates" class="candidate-list">${renderCandidateRows(draft.candidates)}</div>
                <label class="field"><span>Search library</span>
                    <input id="sp-search" type="search" class="emby-input" placeholder="Type at least 2 characters…"></label>
                <div id="sp-search-results" class="search-results"></div>`;

            bindCandidateRowActions(draft, 'sp-candidates');
            let timer;
            document.getElementById('sp-search').oninput = (ev) => {
                clearTimeout(timer);
                timer = setTimeout(async () => {
                    const q = ev.target.value;
                    const resultsEl = document.getElementById('sp-search-results');
                    if (!q || q.trim().length < 2) {
                        resultsEl.innerHTML = '';
                        return;
                    }
                    const params = new URLSearchParams({ q: q.trim(), limit: '20' });
                    if (channel) params.set('contentType', channel.contentType);
                    const results = await api('/catalog/search?' + params.toString());
                    resultsEl.innerHTML = (results || []).map((item) =>
                        `<div class="search-result" data-id="${item.id}">
                            <strong>${escapeHtml(item.name)}</strong>
                            <div class="sub">${escapeHtml(item.type)}</div>
                        </div>`).join('') || '<div class="search-result">No matches</div>';
                    resultsEl.querySelectorAll('.search-result[data-id]').forEach((row) => {
                        row.onclick = () => {
                            itemTitleCache[row.dataset.id] = row.querySelector('strong').textContent;
                            draft.candidates.push({ kind: 0, jellyfinItemId: row.dataset.id, weight: 1, sortOrder: draft.candidates.length });
                            refreshCandidateList(draft, 'sp-candidates');
                        };
                    });
                }, 250);
            };
        }

        document.getElementById('sp-content-mode').onchange = renderContentPanel;
        renderContentPanel();

        document.getElementById('sp-cancel').onclick = closeModal;
        document.getElementById('sp-save').onclick = async () => {
            const name = document.getElementById('sp-name').value.trim();
            if (!name) {
                toast('Presentation name is required.', 'error');
                return;
            }

            const mode = parseInt(document.getElementById('sp-content-mode').value, 10);
            let candidates = [];
            if (mode === 1) {
                candidates = [{ kind: 2, filterJson: buildRuleFilterJson(), weight: 1, sortOrder: 0 }];
            } else if (mode === 2) {
                const listId = document.getElementById('sp-list-id').value;
                if (!listId) {
                    toast('Select a ChannelFlow list.', 'error');
                    return;
                }
                candidates = [{ kind: 3, finTvListId: listId, weight: 1, sortOrder: 0 }];
            } else {
                candidates = draft.candidates;
            }

            if (!candidates.length) {
                toast('Add at least one content candidate.', 'error');
                return;
            }

            const payload = {
                name,
                enabled: document.getElementById('sp-enabled').checked,
                dayOfWeek: parseInt(document.getElementById('sp-day').value, 10),
                slotIndex: slotIndexFromTime(document.getElementById('sp-time').value),
                spanSlots: Math.max(1, Math.min(8, parseInt(document.getElementById('sp-span').value, 10) || 1)),
                candidates
            };

            try {
                if (existing) {
                    await api('/special-presentations/' + existing.id, { method: 'PUT', body: JSON.stringify(payload) });
                } else {
                    await api('/special-presentations/' + specialChannelId, { method: 'POST', body: JSON.stringify(payload) });
                }
                closeModal();
                toast('Special presentation saved.', 'success');
                await loadSpecialPresentations();
            } catch (err) {
                reportApiError(err, 'Could not save special presentation.');
            }
        };
    }

    async function deleteSpecialPresentation(id) {
        if (!confirm('Delete this special presentation?')) return;
        try {
            await api('/special-presentations/' + id, { method: 'DELETE' });
            toast('Special presentation deleted.', 'success');
            await loadSpecialPresentations();
        } catch (err) {
            reportApiError(err, 'Could not delete special presentation.');
        }
    }

    function syncConfigPageFromEvent(event) {
        const page = normalizeConfigPageRoot(event && event.target);
        if (page) {
            configPage = page;
            return page;
        }

        return syncConfigPage();
    }

    function stopGuideClock() {
        if (guideTimer) {
            clearInterval(guideTimer);
            guideTimer = null;
        }
    }

    function startGuideClock() {
        stopGuideClock();
        positionGuideNowLine();
        guideTimer = setInterval(positionGuideNowLine, 15000);
    }

    function guideTimeZone() {
        return (guideData && guideData.timeZone) || scheduleTimeZone || undefined;
    }

    function formatGuideClock(iso) {
        try {
            return new Date(iso).toLocaleTimeString([], {
                hour: 'numeric',
                minute: '2-digit',
                timeZone: guideTimeZone()
            });
        } catch (ignore) {
            return new Date(iso).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
        }
    }

    function formatGuideDate(iso) {
        try {
            return new Intl.DateTimeFormat('en-CA', {
                timeZone: guideTimeZone(),
                year: 'numeric',
                month: '2-digit',
                day: '2-digit'
            }).format(new Date(iso));
        } catch (ignore) {
            return new Date(iso).toISOString().slice(0, 10);
        }
    }

    function isGuideTabVisible() {
        const tab = $('tab-guide');
        return !!(tab && !tab.classList.contains('hidden') && !tab.hidden);
    }

    function todayGuideDate() {
        return formatGuideDate(new Date().toISOString());
    }

    function isGuideViewingToday() {
        if (guideDateFilter) {
            return guideDateFilter === todayGuideDate();
        }
        if (guideData && guideData.from) {
            return formatGuideDate(guideData.from) === todayGuideDate();
        }
        return !guideFromIso;
    }

    function addGuideDate(ymd, days) {
        const parts = (ymd || todayGuideDate()).split('-').map(Number);
        const dt = new Date(Date.UTC(parts[0], (parts[1] || 1) - 1, (parts[2] || 1) + days));
        const month = String(dt.getUTCMonth() + 1).padStart(2, '0');
        const day = String(dt.getUTCDate()).padStart(2, '0');
        return dt.getUTCFullYear() + '-' + month + '-' + day;
    }

    function formatGuideDayHeading(iso) {
        try {
            return new Date(iso).toLocaleDateString([], {
                weekday: 'long',
                month: 'short',
                day: 'numeric',
                timeZone: guideTimeZone()
            });
        } catch (ignore) {
            return formatGuideDate(iso);
        }
    }

    function refreshGuide() {
        return loadGuide({ quiet: true });
    }

    async function waitForPlayoutRebuild(channelId, maxAttempts) {
        const attempts = maxAttempts || 360;
        for (let attempt = 0; attempt < attempts; attempt++) {
            await new Promise((resolve) => setTimeout(resolve, 5000));
            if (selectedChannelId === channelId) {
                await loadLineupPlayoutStatus();
            }

            if (isGuideTabVisible()) {
                loadGuide({ quiet: true }).catch(() => {});
            }

            const h = await api('/lineups/' + channelId + '/playout-horizon');
            const rebuild = h.rebuild || {};

            if (rebuild.state === 'failed') {
                toast(rebuild.error || 'Playout rebuild failed. Check the ChannelFlow log.', 'error');
                return false;
            }

            if (rebuild.state === 'completed') {
                if (rebuild.hasCoverageNow) {
                    toast('Playout rebuild finished. Live TV guide is active for this channel.', 'success');
                } else if (Number(rebuild.playoutItemCount || 0) > 0) {
                    toast('Playout rebuilt, but nothing is on air right now. Check the guide banner for the next start time.', 'success');
                } else {
                    toast('Rebuild finished but the guide is empty. Fill lineup slots, then rebuild again.', 'error');
                }
                return true;
            }
        }

        toast('Playout rebuild is still running. Open the Guide tab again in a minute.', 'info');
        return false;
    }

    async function loadGuide(options) {
        const root = $('tv-guide');
        if (!root) {
            return;
        }

        const quiet = !!(options && options.quiet);
        const scrollToNow = options && Object.prototype.hasOwnProperty.call(options, 'scrollToNow')
            ? !!options.scrollToNow
            : !quiet && isGuideViewingToday();
        const scroller = root.querySelector('.tv-guide-scroll');
        const savedScroll = quiet && scroller
            ? { left: scroller.scrollLeft, top: scroller.scrollTop }
            : null;
        if (!quiet) {
            root.innerHTML = '<div class="empty-state">Loading guide…</div>';
        }
        try {
            const params = new URLSearchParams();
            if (guideDateFilter) {
                params.set('date', guideDateFilter);
            }
            params.set('_', String(Date.now()));
            guideData = await api('/guide?' + params.toString());
            guideFromIso = null;
            guideDateFilter = formatGuideDate(guideData.from);
            const dateInput = $('guide-date');
            if (dateInput) {
                dateInput.value = guideDateFilter;
            }
            const range = $('guide-range-label');
            if (range) {
                range.textContent = formatGuideDayHeading(guideData.from)
                    + (guideData.timeZone ? ' · ' + guideData.timeZone : '');
            }
            renderGuide();
            startGuideClock();
            if (scrollToNow) {
                scrollGuideToNow();
            } else {
                guideScrollToken++;
                if (savedScroll) {
                    const next = root.querySelector('.tv-guide-scroll');
                    if (next) {
                        next.scrollLeft = savedScroll.left;
                        next.scrollTop = savedScroll.top;
                    }
                }
            }
        } catch (err) {
            reportApiError(err, 'Could not load the TV guide.');
            root.innerHTML = '<div class="empty-state">Could not load the TV guide.</div>';
        }
    }

    function shiftGuideDays(days) {
        const current = guideDateFilter || (guideData ? formatGuideDate(guideData.from) : todayGuideDate());
        guideDateFilter = addGuideDate(current, days);
        guideFromIso = null;
        loadGuide({ scrollToNow: guideDateFilter === todayGuideDate() });
    }

    function jumpGuideToNow() {
        guideFromIso = null;
        guideDateFilter = null;
        loadGuide({ scrollToNow: true });
    }

    function jumpGuideToDate(date) {
        guideFromIso = null;
        guideDateFilter = date || null;
        loadGuide({ scrollToNow: !date || date === todayGuideDate() });
    }

    function guideNowScrollLeft() {
        if (!guideData) {
            return null;
        }
        const from = new Date(guideData.from).getTime();
        const to = new Date(guideData.to).getTime();
        const now = Date.now();
        if (now < from || now > to) {
            return null;
        }
        const nowX = ((now - from) / 60000) * GUIDE_PX_PER_MIN;
        const pastPx = 60 * GUIDE_PX_PER_MIN;
        return Math.max(0, nowX - pastPx);
    }

    function scrollGuideToNow() {
        const token = ++guideScrollToken;
        const target = guideNowScrollLeft();
        if (target == null) {
            return;
        }

        const tryScroll = () => {
            if (token !== guideScrollToken) {
                return false;
            }
            const root = $('tv-guide');
            const scroller = root && root.querySelector('.tv-guide-scroll');
            if (!scroller || !isGuideTabVisible()) {
                return false;
            }
            if (scroller.clientWidth < 8) {
                return false;
            }
            const maxLeft = Math.max(0, scroller.scrollWidth - scroller.clientWidth);
            if (target > 32 && maxLeft < 32) {
                return false;
            }
            const left = Math.min(target, maxLeft);
            scroller.scrollLeft = left;
            return Math.abs(scroller.scrollLeft - left) <= 8;
        };

        const retry = (attempt) => {
            if (token !== guideScrollToken) {
                return;
            }
            if (tryScroll() || attempt >= 24) {
                return;
            }
            requestAnimationFrame(() => retry(attempt + 1));
        };

        requestAnimationFrame(() => retry(0));
        [50, 150, 400].forEach((ms) => {
            setTimeout(() => {
                if (token === guideScrollToken) {
                    tryScroll();
                }
            }, ms);
        });
    }

    function positionGuideNowLine() {
        if (!guideData) {
            return;
        }
        const from = new Date(guideData.from).getTime();
        const to = new Date(guideData.to).getTime();
        const now = Date.now();
        const inWindow = now >= from && now <= to;
        const left = ((now - from) / 60000 * GUIDE_PX_PER_MIN) + 'px';
        qa('.tv-guide-now, .tv-guide-now-bar').forEach((line) => {
            line.classList.toggle('hidden', !inWindow);
            if (inWindow) {
                line.style.left = left;
            }
        });
    }

    function renderGuide() {
        const root = $('tv-guide');
        if (!root || !guideData) {
            return;
        }

        const channels = guideData.channels || [];
        const programs = guideData.programs || [];
        if (!channels.length) {
            root.innerHTML = '<div class="empty-state">No enabled channels. Create channels first.</div>';
            return;
        }

        const from = new Date(guideData.from).getTime();
        const to = new Date(guideData.to).getTime();
        const totalMin = Math.max(30, (to - from) / 60000);
        const width = totalMin * GUIDE_PX_PER_MIN;
        const byChannel = {};
        programs.forEach((p) => {
            const key = p.channelId;
            if (!byChannel[key]) {
                byChannel[key] = [];
            }
            byChannel[key].push(p);
        });

        let ticks = '';
        for (let m = 0; m < totalMin; m += 30) {
            ticks += '<div class="tv-guide-tick" style="left:' + (m * GUIDE_PX_PER_MIN) + 'px">'
                + escapeHtml(formatGuideClock(new Date(from + m * 60000).toISOString()))
                + '</div>';
        }

        const rows = channels.map((ch) => {
            const blocks = (byChannel[ch.id] || []).map((p) => {
                const start = new Date(p.start).getTime();
                const finish = new Date(p.finish).getTime();
                const left = Math.max(0, (start - from) / 60000) * GUIDE_PX_PER_MIN;
                const visibleEnd = Math.min(to, finish);
                const visibleStart = Math.max(from, start);
                const w = Math.max(10, (visibleEnd - visibleStart) / 60000 * GUIDE_PX_PER_MIN - 2);
                const now = Date.now();
                const isNow = start <= now && finish > now;
                const type = String(ch.contentType || '').toLowerCase();
                return '<button type="button" class="tv-guide-block type-' + escapeHtml(type)
                    + (isNow ? ' is-now' : '') + (p.isVirtual ? ' is-virtual' : '') + '"'
                    + ' data-program="' + escapeHtml(p.id) + '"'
                    + ' style="left:' + left + 'px;width:' + w + 'px">'
                    + '<strong>' + escapeHtml(p.title || 'Untitled') + '</strong>'
                    + (p.subTitle ? '<span>' + escapeHtml(p.subTitle) + '</span>' : '')
                    + '</button>';
            }).join('');

            const logo = ch.logoUrl
                ? '<span class="tv-guide-logo"><img src="' + escapeHtml(ch.logoUrl) + '" alt="" loading="lazy"></span>'
                : '<span class="tv-guide-logo tv-guide-logo-fallback"></span>';
            return '<div class="tv-guide-row">'
                + '<button type="button" class="tv-guide-channel" data-channel="' + escapeHtml(ch.id) + '">'
                + logo
                + '<div><div class="num">' + escapeHtml(ch.number) + '</div>'
                + '<div class="name">' + escapeHtml(ch.name) + '</div></div>'
                + '</button>'
                + '<div class="tv-guide-track" style="width:' + width + 'px"><div class="tv-guide-now-bar"></div>' + blocks + '</div>'
                + '</div>';
        }).join('');

        const dayWidth = GUIDE_CHANNEL_COL + width;
        root.innerHTML = '<div class="tv-guide-scroll">'
            + '<div class="tv-guide-header" style="width:' + dayWidth + 'px">'
            + '<div class="tv-guide-corner">Channel</div>'
            + '<div class="tv-guide-times"><div class="tv-guide-times-inner" style="width:' + width + 'px">'
            + ticks + '<div class="tv-guide-now"></div></div></div>'
            + '</div>'
            + '<div class="tv-guide-body">' + rows + '</div>'
            + '</div>';

        root.querySelectorAll('[data-program]').forEach((btn) => {
            btn.onclick = () => openGuideProgram(btn.dataset.program);
        });
        root.querySelectorAll('[data-channel]').forEach((btn) => {
            btn.onclick = () => {
                selectedChannelId = btn.dataset.channel;
                switchTab('lineups');
            };
        });
        positionGuideNowLine();
    }

    function guidePosterSrc(url) {
        if (!url) {
            return '';
        }
        if (/^https?:\/\//i.test(url)) {
            return url;
        }
        return resolveUrl(url);
    }

    function openGuideProgram(id) {
        const program = (guideData && guideData.programs || []).find((p) => p.id === id);
        if (!program) {
            return;
        }
        const channel = (guideData.channels || []).find((c) => c.id === program.channelId);
        const when = formatGuideClock(program.start) + ' – ' + formatGuideClock(program.finish);
        const meta = [program.episode, program.year, program.rating, (program.categories || []).join(', ')]
            .filter(Boolean)
            .join(' · ');
        const posterSrc = guidePosterSrc(program.posterUrl);
        const poster = posterSrc
            ? '<img class="tv-guide-poster" src="' + escapeHtml(posterSrc) + '" alt="" onerror="this.remove()">'
            : '';
        openModal(
            program.title || 'Programme',
            '<div class="tv-guide-program">'
                + poster
                + '<div class="tv-guide-program-meta">'
                + '<p class="hint">' + escapeHtml((channel ? channel.number + ' · ' + channel.name + ' · ' : '') + when) + '</p>'
                + (meta ? '<p class="hint">' + escapeHtml(meta) + '</p>' : '')
                + renderGuideProgramDetails(program)
                + '</div></div>',
            '<button type="button" class="emby-button" id="btn-guide-to-lineup">Open lineup</button>'
                + '<button type="button" class="raised button-submit emby-button" id="btn-guide-close-modal">Close</button>'
        );
        const closeBtn = $('btn-guide-close-modal');
        if (closeBtn) {
            closeBtn.onclick = closeModal;
        }
        const lineupBtn = $('btn-guide-to-lineup');
        if (lineupBtn) {
            lineupBtn.onclick = () => {
                closeModal();
                selectedChannelId = program.channelId;
                switchTab('lineups');
            };
        }
    }

    function renderGuideProgramDetails(program) {
        const episodes = Array.isArray(program.episodes) ? program.episodes : [];
        if (episodes.length) {
            return '<div class="tv-guide-episodes">'
                + episodes.map((ep) => {
                    const title = escapeHtml(ep.title || 'Episode');
                    const epNum = ep.episode ? '<p class="hint">' + escapeHtml(ep.episode) + '</p>' : '';
                    const desc = ep.description
                        ? '<p>' + escapeHtml(ep.description) + '</p>'
                        : '';
                    return '<div class="tv-guide-episode"><h4>' + title + '</h4>' + epNum + desc + '</div>';
                }).join('')
                + '</div>';
        }

        return (program.subTitle ? '<h4>' + escapeHtml(program.subTitle) + '</h4>' : '')
            + (program.description ? '<p>' + escapeHtml(program.description) + '</p>' : '<p class="hint">No description.</p>');
    }

    const TAB_PATHS = {
        guide: '/guide',
        channels: '/channels',
        presets: '/presets',
        lineups: '/lineups',
        list: '/lists',
        jellyfin: '/library',
        special: '/special',
        commercials: '/commercials',
        commercialbrainz: '/commercialbrainz',
        youtube: '/youtube',
        ebs: '/ebs',
        emergency: '/emergency',
        ai: '/ai',
        weather: '/weather',
        news: '/news',
        transcode: '/transcode',
        general: '/general',
        quickpin: '/quick-pin',
        clients: '/clients',
        tasks: '/tasks',
        about: '/about',
        credits: '/credits'
    };

    const TAB_TITLES = {
        guide: 'TV Guide',
        channels: 'Channels',
        presets: 'Presets',
        lineups: 'Lineups',
        list: 'Lists',
        jellyfin: 'Library',
        special: 'Special Presentation',
        commercials: 'Commercials',
        commercialbrainz: 'CommercialBrainz',
        youtube: 'YouTube',
        ebs: 'Off Air',
        emergency: 'Emergency Broadcast System',
        ai: 'AI',
        weather: 'Weather',
        news: 'News',
        transcode: 'Transcode',
        general: 'General',
        quickpin: 'Quick Pin',
        clients: 'Clients',
        tasks: 'Tasks',
        about: 'About',
        credits: 'Credits'
    };

    const TAB_SUBTITLES = {
        guide: 'What\'s on now across ChannelFlow-Server channels',
        channels: 'Manage Live TV channels',
        presets: 'Create the Binarygeek119 ready-made lineup',
        lineups: 'Edit 24-hour schedules and playout',
        list: 'Jellyfin playlists, music-video artists, and YouTube playlist imports',
        jellyfin: 'Media servers, libraries, remaps, and removed items',
        special: 'Recurring blocks that override the normal lineup',
        commercials: 'Jellyfin commercials, saved playlists, and channel mapping',
        commercialbrainz: 'YouTube commercial pool from CommercialBrainz',
        youtube: 'YouTube cookies, Premium playback, and SponsorBlock',
        ebs: 'Playback when a channel has nothing scheduled',
        emergency: 'NOAA watches and warnings on TV, movies, and music',
        ai: 'AI lineup generation and tagging',
        weather: 'WeatherStar live channels',
        news: 'FlowWire News',
        transcode: 'Format and encoder for the live MPEG-TS pipeline',
        general: 'Server-wide ChannelFlow-Server settings',
        quickpin: 'Connect ChannelFlow TV with the pin it shows',
        clients: 'Paired ChannelFlow TV apps and their API keys',
        tasks: 'Rebuild playouts, clear the guide, and maintenance',
        about: 'Version, system, and transcode information',
        credits: 'People and projects ChannelFlow builds on'
    };

    function normalizePathname(pathname) {
        let path = String(pathname || '/').split('?')[0];
        const prefix = appPathBase();
        if (prefix && (path === prefix || path.startsWith(prefix + '/'))) {
            path = path.slice(prefix.length) || '/';
        }
        if (path.length > 1) {
            path = path.replace(/\/+$/, '');
        }
        return path || '/';
    }

    function tabFromPath(pathname) {
        const path = normalizePathname(pathname);
        if (path === '/' || path === '/index.html' || path === '/login') {
            return 'channels';
        }

        if (path === '/setup') {
            return 'general';
        }

        for (const name of Object.keys(TAB_PATHS)) {
            if (TAB_PATHS[name] === path) {
                return name;
            }
        }

        if (path === '/list') {
            return 'list';
        }

        if (path === '/jellyfin' || path === '/library' || path.startsWith('/library/')) {
            return 'jellyfin';
        }

        if (path === '/normalization') {
            return 'transcode';
        }

        return 'channels';
    }

    function isModifiedClick(event) {
        return event.metaKey || event.ctrlKey || event.shiftKey || event.altKey || event.button !== 0;
    }

    function onTabLinkClick(event) {
        const link = event.target.closest('a[data-tab]');
        if (!link || isModifiedClick(event) || !TAB_PATHS[link.dataset.tab]) {
            return;
        }

        event.preventDefault();
        switchTab(link.dataset.tab);
    }

    let tabRoutesBound = false;

    function bindTabRoutes() {
        if (tabRoutesBound) {
            return;
        }

        tabRoutesBound = true;
        document.addEventListener('click', onTabLinkClick);
        window.addEventListener('popstate', () => {
            const editorId = channelEditorIdFromPath(location.pathname);
            if (editorId) {
                openDeepChannelEditor(editorId, { fromRoute: true }).catch((err) => reportApiError(err, 'Could not open channel editor.'));
                return;
            }

            closeDeepChannelEditor({ skipHistory: true });
            libraryPage = libraryPageFromPath(location.pathname);
            switchTab(tabFromPath(location.pathname), { skipHistory: true });
        });
    }

    function applyTabFromLocation() {
        const path = normalizePathname(location.pathname);
        if (path === '/login' || channelEditorIdFromPath(path)) {
            return;
        }

        const tab = tabFromPath(path);
        if (tab === 'jellyfin') {
            libraryPage = libraryPageFromPath(path);
        }

        switchTab(tab, { skipHistory: true });
        const app = document.getElementById('app-shell');
        if (app && !app.classList.contains('hidden') && (path === '/' || path === '/index.html' || path === '/setup')) {
            history.replaceState({ tab }, '', withAppBase(TAB_PATHS[tab]));
        } else if (app && !app.classList.contains('hidden') && tab === 'jellyfin') {
            const canonical = libraryPath(libraryPage);
            if (normalizePathname(location.pathname) !== canonical) {
                history.replaceState({ tab, libraryPage }, '', withAppBase(canonical));
            }
        }
    }

    function switchTab(name, options) {
        options = options || {};
        if (name === 'normalization') {
            name = 'transcode';
        }

        if (!TAB_PATHS[name]) {
            name = 'channels';
        }

        if (!syncConfigPage()) {
            return;
        }

        if (!options.keepEditor) {
            closeDeepChannelEditor({ skipHistory: true, stay: true });
        }

        qa('.channelflow-tabs .tab').forEach((t) => {
            t.classList.toggle('active', t.dataset.tab === name);
        });
        document.querySelectorAll('.tab-panel').forEach((p) => {
            const panelId = name === 'transcode' ? 'tab-stream' : ('tab-' + name);
            const on = p.id === panelId;
            p.classList.toggle('active', on);
            p.classList.toggle('hidden', !on);
            p.hidden = !on;
        });
        stopOnAirPolling();
        stopGuideClock();
        if (name === 'channels') startOnAirPolling();
        document.body.classList.toggle('guide-tab-open', name === 'guide');
        if (name === 'guide') {
            loadGuide({ scrollToNow: isGuideViewingToday() });
        }
        if (name === 'general') loadGeneral();
        if (name === 'clients') loadPairedClients();
        if (name === 'ebs') loadEbs();
        if (name === 'emergency') loadWeather();
        if (name === 'ai') loadAi();
        if (name === 'weather') loadWeather();
        if (name === 'news') loadNews();
        if (name === 'transcode') loadStreamOutput();
        if (name === 'about') loadAbout();
        if (name === 'presets') loadPresets();
        if (name === 'lineups') loadLineups();
        if (name === 'list') loadLists();
        if (name === 'jellyfin') {
            if (!options.skipHistory && tabFromPath(location.pathname) !== 'jellyfin') {
                libraryPage = 'connections';
            }

            loadLibraryTab();
        }
        if (name === 'tasks') {
            loadCatalogCleanup();
            loadLibraryScan();
            loadFfprobeScan();
            loadTrueAspectScan();
            loadCommercialBreakScan();
            api('/news/settings').then((settings) => renderNewsBulletinStatus(settings.bulletin)).catch(() => {});
        }
        if (name === 'special') loadSpecialPresentations();
        if (name === 'commercials') {
            applyCommercialsPage();
            loadCommercials();
        }
        if (name === 'commercialbrainz') loadCommercialBrainz();
        if (name === 'youtube') loadYouTube();

        const path = name === 'jellyfin' ? libraryPath(libraryPage) : TAB_PATHS[name];
        if (!options.skipHistory && normalizePathname(location.pathname) !== path) {
            history.pushState({ tab: name, libraryPage: name === 'jellyfin' ? libraryPage : undefined }, '', withAppBase(path));
        }

        document.title = (TAB_TITLES[name] || name) + ' · ChannelFlow-Server';
        window.dispatchEvent(new CustomEvent('channelflow-tabchange', {
            detail: {
                tab: name,
                title: TAB_TITLES[name],
                subtitle: name === 'commercials'
                    ? commercialsSubtitle()
                    : name === 'jellyfin'
                        ? librarySubtitle()
                        : TAB_SUBTITLES[name]
            }
        }));
    }

    async function copySetupUrl(kind, scope) {
        try {
            const urls = await api('/setup/urls');
            const isM3u = kind === 'm3u';
            const local = scope === 'local';
            const text = isM3u
                ? (local ? (urls?.m3uLocal || '') : (urls?.m3u || ''))
                : (local ? (urls?.epgLocal || '') : (urls?.epg || ''));
            if (!text) {
                throw new Error(isM3u ? 'M3U URL is not ready.' : 'XMLTV URL is not ready.');
            }
            const where = local ? 'local' : 'public';
            copyToClipboard(text, `Copied ${isM3u ? 'M3U tuner URL' : 'XMLTV guide URL'} (${where}).`);
        } catch (err) {
            toast(err.message || 'Could not copy URL.', 'error');
        }
    }

    function closeCopyMenus(except) {
        document.querySelectorAll('.copy-menu-panel').forEach((panel) => {
            if (panel === except) {
                return;
            }
            panel.classList.add('hidden');
            const button = panel.parentElement && panel.parentElement.querySelector('.emby-button');
            if (button) {
                button.setAttribute('aria-expanded', 'false');
            }
        });
    }

    function toggleCopyMenu(button) {
        const menu = button.parentElement;
        if (!menu) {
            return;
        }
        const panel = menu.querySelector('.copy-menu-panel');
        if (!panel) {
            return;
        }
        const willOpen = panel.classList.contains('hidden');
        closeCopyMenus(willOpen ? panel : null);
        panel.classList.toggle('hidden', !willOpen);
        button.setAttribute('aria-expanded', String(willOpen));
    }

    function bindCopyMenus() {
        [['btn-copy-m3u', 'm3u'], ['btn-copy-xmltv', 'xmltv']].forEach(([id, kind]) => {
            const button = $(id);
            if (button) {
                button.onclick = (event) => {
                    event.stopPropagation();
                    toggleCopyMenu(button);
                };
            }
        });

        document.querySelectorAll('.copy-menu-panel [data-copy-scope]').forEach((item) => {
            item.onclick = (event) => {
                event.stopPropagation();
                closeCopyMenus();
                copySetupUrl(item.dataset.copyKind, item.dataset.copyScope);
            };
        });

        if (!bindCopyMenus.documentBound) {
            bindCopyMenus.documentBound = true;
            document.addEventListener('click', () => closeCopyMenus());
        }
    }

    function copyToClipboard(text, successMessage) {
        navigator.clipboard.writeText(text).then(() => toast(successMessage || 'Copied to clipboard.', 'success')).catch(() => {
            window.prompt('Copy URL:', text);
        });
    }

    function isActiveConfigPage(candidate) {
        if (!candidate || !document.contains(candidate)) {
            return false;
        }

        if (candidate.classList.contains('hide')
            || candidate.classList.contains('hidden')
            || candidate.getAttribute('aria-hidden') === 'true') {
            return false;
        }

        const rect = candidate.getBoundingClientRect();
        if (rect.width > 0 && rect.height > 0) {
            return true;
        }

        return candidate.classList.contains('active')
            || candidate.classList.contains('mainAnimatedPage');
    }

    function decorateCheckboxes() {
        qa('.checkbox-field').forEach((label) => {
            if (label.querySelector('.channelflow-check-box')) {
                return;
            }

            const input = label.querySelector('input[type="checkbox"]');
            if (!input) {
                return;
            }

            const box = document.createElement('span');
            box.className = 'channelflow-check-box';
            box.setAttribute('aria-hidden', 'true');
            input.insertAdjacentElement('afterend', box);
        });
    }

    function bindEvents() {
        if (!configPage) {
            return;
        }

        decorateCheckboxes();

        function click(id, handler) {
            const el = $(id);
            if (el) {
                el.onclick = handler;
            }
        }

        function change(id, handler) {
            const el = $(id);
            if (el) {
                el.onchange = handler;
            }
        }

        bindTabRoutes();
        click('btn-guide-prev', () => shiftGuideDays(-1));
        click('btn-guide-next', () => shiftGuideDays(1));
        click('btn-guide-now', () => jumpGuideToNow());
        change('guide-date', (e) => jumpGuideToDate(e.target.value));
        click('btn-new-channel', () => openNewChannelForm());
        click('btn-close-channel', () => showChannelForm(false));
        click('btn-cancel-channel', () => showChannelForm(false));
        click('btn-delete-channel', () => deleteChannel(editingChannelId));
        click('btn-deep-ch-back', () => {
            closeDeepChannelEditor();
            switchTab('channels');
        });
        click('btn-deep-ch-save', () => saveDeepChannel().catch((err) => reportApiError(err, 'Could not save channel.')));
        click('btn-deep-ch-add-playlist', addDeepChannelPlaylist);
        click('btn-deep-ch-open-map', () => openMapChannelsForChannel(deepEditingChannelId));
        qa('[data-commercials-page]').forEach((btn) => {
            btn.onclick = () => switchCommercialsPage(btn.dataset.commercialsPage);
        });
        change('map-ch-select', loadMapChannelAssignment);
        click('btn-map-ch-add-playlist', addMapChannelPlaylist);
        click('btn-map-ch-save', () => saveChannelPlaylistMap().catch((err) => reportApiError(err, 'Could not save channel mapping.')));
        const channelForm = $('channel-form');
        if (channelForm) {
            channelForm.onsubmit = saveChannel;
        }
        change('ch-content-type', toggleWeatherFields);
        change('deep-ch-content-type', toggleWeatherFields);
        change('ch-logo-set', () => populateLogoSelectors({ logoSetId: $('ch-logo-set').value, logoFileName: '' }));
        change('deep-ch-logo-set', () => populateLogoSelectors({ logoSetId: $('deep-ch-logo-set').value, logoFileName: '' }, 'deep-ch'));
        const channelFilterEl = $('channel-filter');
        if (channelFilterEl) {
            channelFilterEl.oninput = (e) => { channelFilter = e.target.value.trim(); renderChannelsList(); };
        }

        change('lineup-channel-select', loadLineups);
        click('btn-save-lineup', saveLineup);
        click('btn-rebuild-lineup', rebuildLineup);
        click('btn-preview-lineup', previewLineup);
        click('btn-add-override', openOverrideForm);
        click('btn-add-list', () => openListForm().catch((e) => toast(e.message, 'error')));
        document.querySelectorAll('#list-inner-tabs .inner-tab').forEach((tab) => {
            tab.addEventListener('click', () => {
                listPage = tab.dataset.listPage || 'lists';
                applyListPage();
            });
        });
        document.querySelectorAll('#ai-inner-tabs .inner-tab').forEach((tab) => {
            tab.addEventListener('click', () => {
                aiPage = tab.dataset.aiPage || 'lineups';
                applyAiPage();
            });
        });
        change('ai-primetime-channel', () => {
            loadPrimetimeEditor().catch((err) => toast(err.message, 'error'));
        });
        const primetimeSearchEl = $('ai-primetime-search');
        if (primetimeSearchEl) {
            primetimeSearchEl.oninput = () => {
                primetimeSearch = primetimeSearchEl.value || '';
                renderPrimetimeEditor();
            };
        }
        click('btn-ai-primetime-save', () => savePrimetimeAssignments().catch((err) => toast(err.message, 'error')));
        click('btn-add-media-server', () => addMediaServer().catch((e) => toast(e.message, 'error')));
        change('ms-kind', toggleMediaServerAddFields);
        document.getElementById('library-inner-tabs')?.querySelectorAll('.inner-tab').forEach((tab) => {
            tab.addEventListener('click', (event) => {
                if (isModifiedClick(event)) {
                    return;
                }

                event.preventDefault();
                switchLibraryPage(tab.dataset.libraryPage);
            });
        });
        click('btn-library-save-cleanup', () => {
            const days = $('library-cleanup-grace')?.value;
            if ($('catalog-cleanup-grace') && days != null) {
                $('catalog-cleanup-grace').value = days;
            }
            saveCatalogCleanupSettings().then(() => loadLibraryRemoved()).catch((e) => toast(e.message, 'error'));
        });
        click('btn-library-run-cleanup', () => runCatalogCleanup().then(() => loadLibraryRemoved()).catch((e) => toast(e.message, 'error')));
        click('btn-library-scan-local', () => runCatalogLocalScan().then(() => loadLibraryRemoved()).catch((e) => toast(e.message, 'error')));
        click('btn-add-special', () => openSpecialPresentationForm().catch((e) => toast(e.message, 'error')));
        change('special-channel-select', loadSpecialPresentations);

        click('btn-sync-commercials', () => api('/commercials/sync', { method: 'POST' })
            .then(() => { toast('Commercial sync started.', 'success'); return loadCommercials(); })
            .catch((e) => toast(e.message, 'error')));
        click('btn-scan-blackframes', () => api('/commercials/scan-blackframes', { method: 'POST' })
            .then(() => { toast('Blackframe scan started.', 'success'); return loadCommercials(); })
            .catch((e) => toast(e.message, 'error')));
        click('btn-save-brainz', () => openSavePlaylistModal().catch((e) => toast(e.message, 'error')));
        click('btn-preview-brainz', () => previewBrainz().catch((e) => toast(e.message, 'error')));
        click('btn-sync-brainz', () => syncBrainz().catch((e) => toast(e.message, 'error')));
        click('btn-save-youtube', () => saveYouTubeSettings().catch((e) => toast(e.message, 'error')));
        click('btn-test-youtube', () => testYouTubeAccess().catch((e) => toast(e.message, 'error')));
        click('btn-clear-youtube-cookies', () => clearYouTubeCookies().catch((e) => toast(e.message, 'error')));
        const ytCookieFile = $('yt-cookies-file');
        if (ytCookieFile) {
            ytCookieFile.addEventListener('change', () => {
                const file = ytCookieFile.files && ytCookieFile.files[0];
                const box = $('yt-cookies');
                if (!file || !box) {
                    return;
                }

                file.text().then((text) => {
                    box.value = text || '';
                    toast('Loaded ' + file.name + '. Click Save YouTube settings to keep it.', 'success');
                }).catch((err) => toast(err.message || 'Could not read cookies file.', 'error'));
            });
        }
        click('btn-rebuild-all', () => api('/tasks/rebuild-all', { method: 'POST' })
            .then(() => {
                toast('Rebuild all started in background. This may take several minutes.', 'success');
                $('task-status').textContent = 'Rebuild all playouts running in background…';
            })
            .catch((e) => toast(e.message, 'error')));
        click('btn-clear-guide', () => {
            if (!confirm('Delete all Live TV guide playout so you can start fresh? Channels go Off Air until you Rebuild All Playouts. Channel lineups are kept.')) {
                return;
            }
            api('/tasks/clear-guide', { method: 'POST' })
                .then((data) => {
                    const count = Number(data.cleared || 0);
                    toast(`Cleared ${count} guide item${count === 1 ? '' : 's'}. Rebuild All Playouts to fill the schedule again.`, 'success');
                    $('task-status').textContent = `Cleared ${count} playout items. Channels are Off Air until you rebuild.`;
                })
                .catch((e) => toast(e.message, 'error'));
        });
        click('btn-force-commercial', () => forceWatchedChannelsToCommercial().catch((e) => toast(e.message, 'error')));
        click('btn-save-catalog-cleanup', () => saveCatalogCleanupSettings().catch((e) => toast(e.message, 'error')));
        click('btn-run-catalog-cleanup', () => runCatalogCleanup().catch((e) => toast(e.message, 'error')));
        click('btn-scan-local-catalog', () => runCatalogLocalScan().catch((e) => toast(e.message, 'error')));
        click('btn-run-library-scan', () => runLibraryScan().catch((e) => toast(e.message, 'error')));
        click('btn-run-ffprobe-scan', () => runFfprobeScan().catch((e) => toast(e.message, 'error')));
        click('btn-run-true-aspect-scan', () => runTrueAspectScan().catch((e) => toast(e.message, 'error')));
        click('btn-run-commercial-breaks-scan', () => runCommercialBreakScan().catch((e) => toast(e.message, 'error')));
        click('btn-commercial-breaks-settings', () => {
            const open = () => openCommercialBreakSettings();
            if (commercialBreakSettings) {
                open();
                return;
            }
            api('/tasks/commercial-breaks/settings').then((settings) => {
                applyCommercialBreakSwitches(settings);
                open();
            }).catch((e) => toast(e.message, 'error'));
        });
        const commercialScanSwitch = $('commercial-breaks-scan-enabled');
        if (commercialScanSwitch) {
            commercialScanSwitch.addEventListener('change', () => {
                saveCommercialBreakSettings({ scanEnabled: commercialScanSwitch.checked })
                    .catch((e) => toast(e.message, 'error'));
            });
        }
        const commercialWriteSwitch = $('commercial-breaks-write-files');
        if (commercialWriteSwitch) {
            commercialWriteSwitch.addEventListener('change', () => {
                saveCommercialBreakSettings({ writeChaptersToFiles: commercialWriteSwitch.checked })
                    .catch((e) => toast(e.message, 'error'));
            });
        }

        bindCopyMenus();
        click('btn-save-general', saveGeneralSettings);
        click('btn-refresh-clients', () => loadPairedClients().catch((e) => toast(e.message, 'error')));
        const clientsTable = $('clients-table');
        if (clientsTable) {
            clientsTable.addEventListener('click', (event) => {
                const button = event.target.closest('[data-remove-client]');
                if (!button) {
                    return;
                }
                removePairedClient(button.getAttribute('data-remove-client')).catch((e) => toast(e.message, 'error'));
            });
        }
        click('btn-refresh-client-logs', () => loadClientLogList().catch((e) => toast(e.message, 'error')));
        const clientLogDevice = $('client-logs-device');
        if (clientLogDevice) {
            clientLogDevice.addEventListener('change', () => {
                const fileSelect = $('client-logs-file');
                if (fileSelect) {
                    fileSelect.innerHTML = '';
                }
                loadClientLogContents().catch((e) => toast(e.message, 'error'));
            });
        }
        const clientLogFile = $('client-logs-file');
        if (clientLogFile) {
            clientLogFile.addEventListener('change', () => {
                loadClientLogContents().catch((e) => toast(e.message, 'error'));
            });
        }
        click('btn-quick-pin-connect', () => redeemQuickPin().catch((e) => toast(e.message, 'error')));
        const quickPinInput = $('quick-pin-input');
        if (quickPinInput) {
            quickPinInput.addEventListener('input', onQuickPinInput);
            quickPinInput.addEventListener('keydown', (event) => {
                if (event.key === 'Enter') {
                    event.preventDefault();
                    redeemQuickPin().catch((e) => toast(e.message, 'error'));
                }
            });
        }
        click('btn-save-ebs', () => saveEbsSettings().catch((e) => toast(e.message, 'error')));
        const musicPacks = $('ebs-music-packs');
        if (musicPacks) {
            musicPacks.addEventListener('click', (event) => {
                const download = event.target.closest('[data-pack-download]');
                if (download) {
                    downloadMusicPack(download.getAttribute('data-pack-download')).catch((e) => toast(e.message, 'error'));
                    return;
                }
                const remove = event.target.closest('[data-pack-remove]');
                if (remove) {
                    removeMusicPack(remove.getAttribute('data-pack-remove')).catch((e) => toast(e.message, 'error'));
                }
            });
        }
        click('btn-save-ai-settings', () => saveAiSettings().catch((e) => toast(e.message, 'error')));
        click('btn-test-ai', () => { void testAiConnection(); });
        bindAiApiKeyFields();
        change('ai-default-provider', onAiProviderChange);
        click('btn-ai-generate-all', () => generateAllAiLineups().catch((e) => toast(e.message, 'error')));
        click('btn-ai-clear-all-pools', () => clearAllAiPools().catch((e) => toast(e.message, 'error')));
        click('btn-ai-cancel-generate-all', () => cancelGenerateAll().catch((e) => toast(e.message, 'error')));
        click('btn-weather-guide-cache-generate', () => generateWeatherGuideCache().catch((e) => toast(e.message, 'error')));
        click('btn-weather-guide-cache-clear', () => clearWeatherGuideCache().catch((e) => toast(e.message, 'error')));
        click('btn-ai-apply', () => applyAiLineup().catch((e) => toast(e.message, 'error')));
        click('btn-ai-discard', discardAiPreview);
        change('ai-enabled', updateAiUiState);
        click('btn-upload-ebs-usa', () => uploadEbsSlate('usa', 'ebs-usa-file'));
        click('btn-upload-ebs-international', () => uploadEbsSlate('international', 'ebs-international-file'));
        click('btn-remove-ebs-usa', () => removeEbsSlate('usa'));
        click('btn-remove-ebs-international', () => removeEbsSlate('international'));
        click('btn-save-weather', () => saveWeatherSettings());
        click('btn-save-weather-location', () => saveWeatherSettings('Weather location saved.'));
        click('btn-save-weather-source', () => saveWeatherSettings());
        click('btn-save-weather-alerts', () => saveWeatherSettings('Emergency Broadcast System settings saved.'));
        click('btn-test-weather-alerts', () => { void testWeatherAlerts(); });
        click('btn-stop-weather-alert-test', () => { void stopWeatherAlertTest(); });
        change('weather-alert-overlay-mode', toggleWeatherAlertCutInFields);
        click('btn-save-news', () => saveNewsSettings().catch((e) => toast(e.message, 'error')));
        change('news-no-music', syncNewsMusicUi);
        change('news-tts', syncNewsTtsUi);
        click('btn-save-stream', () => saveStreamSettings().catch((e) => toast(e.message, 'error')));
        click('btn-test-transcode', () => testTranscode().catch((e) => toast(e.message, 'error')));
        click('btn-reset-transcode', () => resetTranscodeSettings().catch((e) => toast(e.message, 'error')));
        change('transcode-hwaccel', syncTranscodeUi);
        click('btn-reset-normalization', () => resetNormalizationSettings().catch((e) => toast(e.message, 'error')));
        change('norm-video-codec', () => {
            syncNormalizationUi();
            updateEncoderHint();
        });
        click('btn-save-news-feeds', () => saveNewsFeeds().catch((e) => toast(e.message, 'error')));
        click('btn-add-news-feed', addNewsFeedRow);
        click('btn-preview-news', () => loadNewsPreview(true).catch((e) => toast(e.message, 'error')));
        click('btn-run-news-bulletin', () => runNewsBulletin().catch((e) => toast(e.message, 'error')));
        click('btn-run-news-bulletin-task', () => runNewsBulletin().catch((e) => toast(e.message, 'error')));
        click('btn-cleanup-news-bulletin', () => cleanupNewsBulletins().catch((e) => toast(e.message, 'error')));
        click('btn-cleanup-news-bulletin-task', () => cleanupNewsBulletins().catch((e) => toast(e.message, 'error')));
        change('ebs-display-mode', updateEbsFieldVisibility);
        change('ebs-slate-variant', refreshEbsPreviews);
        change('ebs-audio-mode', updateEbsFieldVisibility);
        change('ebs-music-source', updateEbsFieldVisibility);
        click('btn-apply-presets', () => { void applyPresets(); });
        change('preset-numbering-mode', loadPresets);
        click('catalog-sync-dismiss', () => {
            catalogSyncClosed = true;
            catalogSyncSeenRunning = false;
            if (catalogSyncHideTimer) {
                clearTimeout(catalogSyncHideTimer);
                catalogSyncHideTimer = null;
            }

            hideCatalogSyncPopup();
        });
        bindTaskPopupStack();
        click('modal-close', closeModal);
        const modalBackdrop = $('modal-backdrop');
        if (modalBackdrop) {
            modalBackdrop.onclick = (e) => { if (e.target === modalBackdrop) closeModal(); };
        }

        const previewDate = $('lineup-preview-date');
        if (previewDate && !previewDate.value) {
            previewDate.value = todayIsoDate();
        }
    }

    async function refresh() {
        await Promise.all([
            loadScheduleTimeZone(),
            loadChannels()
        ]);
    }

    async function loadScheduleTimeZone() {
        try {
            const settings = await api('/general/settings');
            setScheduleTimeZone(settings.scheduleTimeZone || settings.ScheduleTimeZone);
        } catch (ignore) {
            startAppClock();
        }
    }

    function init(page) {
        const app = document.getElementById('app-shell');
        if (app && app.classList.contains('hidden')) {
            return Promise.resolve();
        }

        if (!syncConfigPage(page)) {
            return Promise.resolve();
        }

        bindEvents();
        applyTabFromLocation();
        startAppClock();
        startCatalogSyncPolling();
        return refresh().then(() => {
            const editorId = channelEditorIdFromPath(location.pathname);
            if (editorId) {
                return openDeepChannelEditor(editorId, { fromRoute: true });
            }
        }).catch((err) => reportApiError(err, 'Could not load ChannelFlow-Server admin.'));
    }

    function resolveConfigPage(preferred) {
        preferred = normalizeConfigPageRoot(preferred);

        if (isActiveConfigPage(preferred)) {
            return preferred;
        }

        const pages = document.querySelectorAll('#ChannelFlowConfigPage');
        for (let i = pages.length - 1; i >= 0; i--) {
            if (isActiveConfigPage(pages[i])) {
                return pages[i];
            }
        }

        if (preferred && document.contains(preferred)) {
            return preferred;
        }

        return pages.length ? pages[pages.length - 1] : null;
    }

    function bootChannelFlowAdmin(page) {
        page = resolveConfigPage(page);
        if (!page || !window.ChannelFlow || !window.ChannelFlow.init) {
            return false;
        }

        window.ChannelFlow.init(page);
        return true;
    }

    window.ChannelFlow = { init, refresh, loadChannels, bootChannelFlowAdmin, switchTab, tabFromPath };

    document.addEventListener('DOMContentLoaded', function () {
        const app = document.getElementById('app-shell');
        if (app && app.classList.contains('hidden')) {
            return;
        }

        const page = document.getElementById('ChannelFlowConfigPage');
        if (page && window.ChannelFlow) {
            window.ChannelFlow.init(page);
        }
    });
})();
