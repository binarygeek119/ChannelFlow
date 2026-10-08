//! ChannelFlow's model of the ErsatzTV next transcoding pipeline.
//!
//! This mirrors `ffmpeg` + `normalization` in
//! `vendor/ersatztv-next/schema/channel_config.json` — the keys next reads to
//! decide how a stream is encoded, and nothing else. `playout` and `fallback`
//! live in the same document but describe *what* plays rather than *how* it is
//! encoded, so they are deliberately absent here.
//!
//! Two tests at the bottom keep this honest. One expands next's schema into a
//! fully-populated instance and asserts this module can hold every field with
//! the right type. The other asserts [`spec`] names exactly the fields the
//! schema declares, so a field added or renamed upstream fails the build
//! instead of quietly going unwritten.
//!
//! # Defaults and per-channel overrides
//!
//! `<config>/transcode.json` holds the instance defaults — what the Transcode
//! page edits. A channel may carry a sparse override patch (see
//! [`Channel::transcode`](crate::model::Channel)); its effective settings are
//! those defaults with the patch deep-merged on top.
//!
//! In a patch, an absent key inherits and a key present with the value `null`
//! is a *real* value — "force software encode", "let the bitrate be automatic"
//! — not an instruction to inherit. That distinction is why patches are plain
//! JSON rather than a second typed struct: `Option<T>` cannot tell absent from
//! null, and the difference matters here.

use serde::{Deserialize, Serialize};
use serde_json::{json, Map, Value};

/// A transcode document next would reject: an unknown field, the wrong type,
/// or a value outside its schema. Surfaces as `400`, never `500`.
#[derive(Debug)]
pub struct TranscodeError(String);

impl std::fmt::Display for TranscodeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for TranscodeError {}

macro_rules! wire_enum {
    (
        $(#[$meta:meta])*
        $name:ident default $default:ident {
            $($variant:ident => ($wire:literal, $label:literal)),+ $(,)?
        }
    ) => {
        $(#[$meta])*
        #[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
        pub enum $name {
            $(
                #[serde(rename = $wire)]
                $variant,
            )+
        }

        impl $name {
            /// Wire value paired with the label the Transcode page shows,
            /// in next's schema order.
            pub const OPTIONS: &'static [(&'static str, &'static str)] = &[
                $(($wire, $label)),+
            ];
        }

        impl Default for $name {
            fn default() -> Self {
                Self::$default
            }
        }
    };
}

wire_enum! {
    /// Whether items are re-encoded or handed to the segmenter untouched.
    StreamMode default Transcode {
        Transcode => ("transcode", "Transcode always"),
        Copy => ("copy", "Stream copy when possible"),
    }
}

wire_enum! {
    /// Codec for transcoded video.
    VideoFormat default H264 {
        H264 => ("h264", "H.264"),
        Hevc => ("hevc", "H.265 / HEVC"),
        Mpeg2video => ("mpeg2video", "MPEG-2"),
    }
}

wire_enum! {
    /// Codec for transcoded audio.
    AudioFormat default Aac {
        Aac => ("aac", "AAC"),
        Ac3 => ("ac3", "Dolby Digital (AC-3)"),
    }
}

wire_enum! {
    /// Source audio codecs next will copy. Limited to what muxes into HLS MPEG-TS.
    AudioCopyFormat default Aac {
        Aac => ("aac", "AAC"),
        Ac3 => ("ac3", "AC-3"),
        Eac3 => ("eac3", "E-AC-3"),
        Mp2 => ("mp2", "MP2"),
        Mp3 => ("mp3", "MP3"),
    }
}

wire_enum! {
    /// Encoder family. `None` on the field means software.
    HardwareAccel default Vaapi {
        Amf => ("amf", "AMD AMF"),
        Cuda => ("cuda", "NVIDIA CUDA / NVENC"),
        Qsv => ("qsv", "Intel Quick Sync (QSV)"),
        Rkmpp => ("rkmpp", "Rockchip MPP"),
        Vaapi => ("vaapi", "VAAPI (Intel / AMD)"),
        Videotoolbox => ("videotoolbox", "VideoToolbox (macOS)"),
        Vulkan => ("vulkan", "Vulkan"),
    }
}

wire_enum! {
    /// libva backend for the selected VAAPI device.
    VaapiDriver default Ihd {
        Ihd => ("ihd", "Intel iHD"),
        I965 => ("i965", "Intel i965"),
        Radeonsi => ("radeonsi", "AMD radeonsi"),
    }
}

wire_enum! {
    /// How a source is fitted to the target size.
    ScalingMode default ScaleAndPad {
        ScaleAndPad => ("scale_and_pad", "Scale and pad"),
        Stretch => ("stretch", "Stretch"),
        Crop => ("crop", "Crop"),
    }
}

wire_enum! {
    /// What happens to subtitle tracks.
    SubtitleMode default Burn {
        Burn => ("burn", "Burn into video"),
        Convert => ("convert", "Convert to WebVTT"),
    }
}

/// Turns a channel's sparse override patch into a full config.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
#[serde(deny_unknown_fields)]
pub struct TranscodeConfig {
    #[serde(default)]
    pub ffmpeg: FfmpegConfig,
    #[serde(default)]
    pub normalization: NormalizationConfig,
}

/// Paths and filter capability overrides. Blank paths mean the container's
/// `ffmpeg`/`ffprobe` on `PATH`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
#[serde(deny_unknown_fields)]
pub struct FfmpegConfig {
    #[serde(default)]
    pub ffmpeg_path: Option<String>,
    #[serde(default)]
    pub ffprobe_path: Option<String>,
    #[serde(default)]
    pub reports_folder: Option<String>,
    #[serde(default)]
    pub preferred_filters: Vec<String>,
    #[serde(default)]
    pub disabled_filters: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
#[serde(deny_unknown_fields)]
pub struct NormalizationConfig {
    #[serde(default)]
    pub audio: AudioNormalization,
    #[serde(default)]
    pub subtitle: SubtitleNormalization,
    #[serde(default)]
    pub video: VideoNormalization,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct VideoNormalization {
    #[serde(default)]
    pub mode: StreamMode,
    /// `None` means next's own default: H.264 and HEVC.
    #[serde(default = "default_video_copy_formats")]
    pub copy_formats: Option<Vec<VideoFormat>>,
    #[serde(default)]
    pub format: VideoFormat,
    #[serde(default = "default_bit_depth")]
    pub bit_depth: u8,
    #[serde(default)]
    pub bitrate_kbps: Option<u32>,
    #[serde(default)]
    pub buffer_kbps: Option<u32>,
    #[serde(default)]
    pub width: Option<u32>,
    #[serde(default)]
    pub height: Option<u32>,
    #[serde(default)]
    pub scaling_mode: ScalingMode,
    /// `N` or `N/D`, e.g. `25` or `30000/1001`. `None` keeps the source rate.
    #[serde(default)]
    pub frame_rate: Option<String>,
    #[serde(default)]
    pub deinterlace: bool,
    #[serde(default)]
    pub accel: Option<HardwareAccel>,
    #[serde(default)]
    pub vaapi_device: Option<String>,
    #[serde(default)]
    pub vaapi_driver: Option<VaapiDriver>,
    /// Windows only. `None` picks the discrete AMD adapter.
    #[serde(default)]
    pub amf_device: Option<u32>,
    #[serde(default)]
    pub filters: VideoFilterOptions,
}

impl Default for VideoNormalization {
    fn default() -> Self {
        Self {
            mode: StreamMode::default(),
            copy_formats: default_video_copy_formats(),
            format: VideoFormat::default(),
            bit_depth: default_bit_depth(),
            bitrate_kbps: None,
            buffer_kbps: None,
            width: None,
            height: None,
            scaling_mode: ScalingMode::default(),
            frame_rate: None,
            deinterlace: false,
            accel: None,
            vaapi_device: None,
            vaapi_driver: None,
            amf_device: None,
            filters: VideoFilterOptions::default(),
        }
    }
}

/// Per-algorithm tuning. next picks the best deinterlacer and tonemapper the
/// build supports, then applies the matching entry here.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
#[serde(deny_unknown_fields)]
pub struct VideoFilterOptions {
    #[serde(default)]
    pub bwdif: Option<ModeOption>,
    #[serde(default)]
    pub bwdif_cuda: Option<ModeOption>,
    #[serde(default)]
    pub deinterlace_qsv: Option<ModeOption>,
    #[serde(default)]
    pub deinterlace_vaapi: Option<ModeOption>,
    #[serde(default)]
    pub libplacebo: Option<LibplaceboOption>,
    #[serde(default)]
    pub tonemap: Option<TonemapOption>,
    #[serde(default)]
    pub tonemap_opencl: Option<TonemapOption>,
    #[serde(default)]
    pub w3fdif: Option<ModeOption>,
    #[serde(default)]
    pub yadif: Option<ModeOption>,
    #[serde(default)]
    pub yadif_cuda: Option<ModeOption>,
}

/// The `mode` argument shared by yadif, bwdif, w3fdif and the hardware
/// deinterlacers. Blank lets the filter use its own default.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
#[serde(deny_unknown_fields)]
pub struct ModeOption {
    #[serde(default)]
    pub mode: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
#[serde(deny_unknown_fields)]
pub struct TonemapOption {
    #[serde(default)]
    pub tonemap: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
#[serde(deny_unknown_fields)]
pub struct LibplaceboOption {
    #[serde(default)]
    pub tonemapping: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct AudioNormalization {
    #[serde(default)]
    pub mode: StreamMode,
    /// `None` means next's own default: AAC, AC-3, E-AC-3 and MP3.
    #[serde(default = "default_audio_copy_formats")]
    pub copy_formats: Option<Vec<AudioCopyFormat>>,
    #[serde(default)]
    pub format: AudioFormat,
    #[serde(default)]
    pub bitrate_kbps: Option<u32>,
    #[serde(default)]
    pub buffer_kbps: Option<u32>,
    /// Speaker count, e.g. `2` or `6`. `None` keeps the source layout.
    #[serde(default)]
    pub channels: Option<u32>,
    #[serde(default)]
    pub sample_rate_hz: Option<u32>,
    #[serde(default)]
    pub normalize_loudness: bool,
    #[serde(default)]
    pub loudness: Option<AudioLoudness>,
}

impl Default for AudioNormalization {
    fn default() -> Self {
        Self {
            mode: StreamMode::default(),
            copy_formats: default_audio_copy_formats(),
            format: AudioFormat::default(),
            bitrate_kbps: None,
            buffer_kbps: None,
            channels: None,
            sample_rate_hz: None,
            normalize_loudness: false,
            loudness: None,
        }
    }
}

/// EBU R128 targets, in LUFS/LU. `None` on a field uses next's default.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
#[serde(deny_unknown_fields)]
pub struct AudioLoudness {
    #[serde(default)]
    pub integrated_target: Option<f64>,
    #[serde(default)]
    pub range_target: Option<f64>,
    #[serde(default)]
    pub true_peak: Option<f64>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct SubtitleNormalization {
    #[serde(default)]
    pub fonts_folder: Option<String>,
    /// Extra ASS style overrides, e.g. `FontName=Arial`.
    #[serde(default)]
    pub force_style: Option<String>,
    #[serde(default)]
    pub mode: SubtitleMode,
}

impl Default for SubtitleNormalization {
    fn default() -> Self {
        Self {
            fonts_folder: None,
            force_style: None,
            mode: SubtitleMode::default(),
        }
    }
}

fn default_bit_depth() -> u8 {
    8
}

fn default_video_copy_formats() -> Option<Vec<VideoFormat>> {
    Some(vec![VideoFormat::H264, VideoFormat::Hevc])
}

fn default_audio_copy_formats() -> Option<Vec<AudioCopyFormat>> {
    Some(vec![
        AudioCopyFormat::Aac,
        AudioCopyFormat::Ac3,
        AudioCopyFormat::Eac3,
        AudioCopyFormat::Mp3,
    ])
}

impl TranscodeConfig {
    /// Parse and check a whole transcode document — the defaults file, or the
    /// body of a `PUT`.
    pub fn parse(value: &Value) -> Result<Self, TranscodeError> {
        let config: Self = serde_json::from_value(value.clone())
            .map_err(|error| TranscodeError(format!("not a valid transcode document: {error}")))?;
        config.check()?;
        Ok(config)
    }

    /// These defaults with a channel's override patch deep-merged on top.
    ///
    /// Objects merge key by key. Every other value replaces the default
    /// wholesale, including `null`, so a channel can say "no acceleration"
    /// even when the defaults name one. An unknown key is an error rather than
    /// a silently dropped override.
    pub fn merged(&self, overrides: &Value) -> Result<Self, TranscodeError> {
        if !overrides.is_null() && !overrides.is_object() {
            return Err(TranscodeError(
                "transcode overrides must be a JSON object".to_string(),
            ));
        }

        let mut base = serde_json::to_value(self)
            .map_err(|error| TranscodeError(format!("could not read defaults: {error}")))?;
        if let Some(patch) = overrides.as_object() {
            merge_object(&mut base, patch);
        }

        let merged: Self = serde_json::from_value(base).map_err(|error| {
            TranscodeError(format!("overrides do not match next's schema: {error}"))
        })?;
        merged.check()?;
        Ok(merged)
    }

    /// Rules serde cannot express: next's `frame_rate` pattern.
    fn check(&self) -> Result<(), TranscodeError> {
        if let Some(rate) = self.normalization.video.frame_rate.as_deref() {
            if !is_valid_frame_rate(rate) {
                return Err(TranscodeError(format!(
                    "frame rate `{rate}` must be N or N/D with positive integers, e.g. 25 or 30000/1001"
                )));
            }
        }
        Ok(())
    }
}

fn merge_object(base: &mut Value, patch: &Map<String, Value>) {
    let Value::Object(base_map) = base else {
        return;
    };
    for (key, value) in patch {
        match base_map.get_mut(key) {
            Some(existing) if existing.is_object() && value.is_object() => {
                if let Some(nested) = value.as_object() {
                    merge_object(existing, nested);
                }
            }
            Some(existing) => *existing = value.clone(),
            None => {
                base_map.insert(key.clone(), value.clone());
            }
        }
    }
}

/// next's `frame_rate` pattern, `^[1-9][0-9]*(/[1-9][0-9]*)?$`, without
/// pulling in a regular expression engine for one field.
fn is_valid_frame_rate(value: &str) -> bool {
    match value.split_once('/') {
        Some((numerator, denominator)) => {
            is_positive_integer(numerator) && is_positive_integer(denominator)
        }
        None => is_positive_integer(value),
    }
}

fn is_positive_integer(value: &str) -> bool {
    !value.is_empty() && !value.starts_with('0') && value.bytes().all(|byte| byte.is_ascii_digit())
}

/// The Transcode page's field list: what next allows, grouped the way someone
/// actually reasons about it.
///
/// Served rather than hard-coded in the browser so the form cannot drift from
/// the schema — a test asserts these paths are exactly next's.
pub fn spec() -> Value {
    json!({
        "groups": [
            group(
                "mode",
                "Stream mode",
                "Copy hands HLS-compatible sources to the segmenter without re-encoding. \
                 Anything else — and any item with graphics or burned-in subtitles — still \
                 transcodes to the video and audio settings below.",
                vec![
                    enum_field(
                        "normalization.video.mode",
                        "Video",
                        &StreamMode::OPTIONS,
                        None,
                        "Copy items whose source codec is in the list; transcode the rest.",
                    ),
                    enum_list_field(
                        "normalization.video.copy_formats",
                        "Video copy formats",
                        &VideoFormat::OPTIONS,
                        "Source video codecs that may be copied. Empty copies nothing.",
                    ),
                    enum_field(
                        "normalization.audio.mode",
                        "Audio",
                        &StreamMode::OPTIONS,
                        None,
                        "Copy items whose source codec is in the list; transcode the rest.",
                    ),
                    enum_list_field(
                        "normalization.audio.copy_formats",
                        "Audio copy formats",
                        &AudioCopyFormat::OPTIONS,
                        "Source audio codecs that may be copied. Empty copies nothing.",
                    ),
                ],
            ),
            group(
                "video",
                "Video",
                "Applies to every item that is not copied.",
                vec![
                    enum_field(
                        "normalization.video.format",
                        "Codec",
                        &VideoFormat::OPTIONS,
                        None,
                        "Codec for transcoded items.",
                    ),
                    enum_int_field(
                        "normalization.video.bit_depth",
                        "Bit depth",
                        &[(8, "8-bit"), (10, "10-bit")],
                        "10-bit needs a 10-bit-capable encoder. next itself allows any 0-255.",
                    ),
                    int_field(
                        "normalization.video.bitrate_kbps",
                        "Bitrate (kbps)",
                        None,
                        None,
                        Some(1),
                        true,
                        "Blank encodes for quality and picks the bitrate itself.",
                    ),
                    int_field(
                        "normalization.video.buffer_kbps",
                        "Rate-control buffer (kbps)",
                        None,
                        None,
                        Some(1),
                        true,
                        "Blank lets the encoder size its own buffer.",
                    ),
                    int_field(
                        "normalization.video.width",
                        "Width",
                        None,
                        None,
                        Some(2),
                        true,
                        "Blank keeps the source size — set width and height together.",
                    ),
                    int_field(
                        "normalization.video.height",
                        "Height",
                        None,
                        None,
                        Some(2),
                        true,
                        "Blank keeps the source size — set width and height together.",
                    ),
                    enum_field(
                        "normalization.video.scaling_mode",
                        "Scaling",
                        &ScalingMode::OPTIONS,
                        None,
                        "How the source is fitted to the target size.",
                    ),
                    text_field(
                        "normalization.video.frame_rate",
                        "Frame rate",
                        true,
                        "Output rate as N or N/D, e.g. 25 or 30000/1001. Blank keeps the source rate. \
                         In copy mode only items already at this rate are copied.",
                        &["23.976", "24", "25", "29.97", "30", "50", "59.94", "60"],
                    ),
                    bool_field(
                        "normalization.video.deinterlace",
                        "Deinterlace",
                        "Deinterlace interlaced sources. next picks the best filter below that this build supports.",
                    ),
                ],
            ),
            group(
                "accel",
                "Hardware acceleration",
                "Software encoding is always available. Anything else needs the device passed into the container.",
                vec![
                    enum_field(
                        "normalization.video.accel",
                        "Acceleration",
                        &HardwareAccel::OPTIONS,
                        Some("Software (no hardware encoder)"),
                        "Blank is software. next falls back to the software encoder when the device cannot encode.",
                    ),
                    text_field(
                        "normalization.video.vaapi_device",
                        "VAAPI device",
                        true,
                        "Render node, e.g. /dev/dri/renderD128. Blank uses /dev/dri/renderD128.",
                        &["/dev/dri/renderD128", "/dev/dri/renderD129"],
                    ),
                    enum_field(
                        "normalization.video.vaapi_driver",
                        "VAAPI driver",
                        &VaapiDriver::OPTIONS,
                        Some("Automatic"),
                        "Blank lets libva pick the driver for the device.",
                    ),
                    int_field(
                        "normalization.video.amf_device",
                        "AMF device",
                        None,
                        None,
                        Some(1),
                        true,
                        "Windows only. Blank picks the discrete AMD adapter.",
                    ),
                ],
            ),
            group(
                "filters",
                "Video filters",
                "Per-algorithm tuning. Blank leaves each filter on its own default; \
                 the ones that do not match the acceleration above are simply not used.",
                vec![
                    text_field(
                        "normalization.video.filters.bwdif.mode",
                        "BWDIF mode",
                        true,
                        "Software deinterlacer. 0 sends frames, 1 sends fields (double rate).",
                        &["0", "1"],
                    ),
                    text_field(
                        "normalization.video.filters.yadif.mode",
                        "YADIF mode",
                        true,
                        "Software deinterlacer. 0 sends frames, 1 sends fields (double rate).",
                        &["0", "1"],
                    ),
                    text_field(
                        "normalization.video.filters.w3fdif.mode",
                        "W3FDIF mode",
                        true,
                        "Software deinterlacer. ffmpeg accepts 0, 1, or a named mode.",
                        &["0", "1"],
                    ),
                    text_field(
                        "normalization.video.filters.bwdif_cuda.mode",
                        "BWDIF mode (CUDA)",
                        true,
                        "CUDA deinterlacer. 0 sends frames, 1 sends fields.",
                        &["0", "1"],
                    ),
                    text_field(
                        "normalization.video.filters.yadif_cuda.mode",
                        "YADIF mode (CUDA)",
                        true,
                        "CUDA deinterlacer. 0 sends frames, 1 sends fields.",
                        &["0", "1"],
                    ),
                    text_field(
                        "normalization.video.filters.deinterlace_qsv.mode",
                        "Deinterlace mode (QSV)",
                        true,
                        "Quick Sync deinterlacer, e.g. 0 / 1 / 2.",
                        &["0", "1", "2"],
                    ),
                    text_field(
                        "normalization.video.filters.deinterlace_vaapi.mode",
                        "Deinterlace mode (VAAPI)",
                        true,
                        "VAAPI deinterlacer, e.g. 0 / 1 / 2.",
                        &["0", "1", "2"],
                    ),
                    text_field(
                        "normalization.video.filters.tonemap.tonemap",
                        "Tonemap algorithm",
                        true,
                        "Software HDR-to-SDR. e.g. hable, mobius, reinhard, linear.",
                        &["hable", "mobius", "reinhard", "linear", "clip"],
                    ),
                    text_field(
                        "normalization.video.filters.tonemap_opencl.tonemap",
                        "Tonemap algorithm (OpenCL)",
                        true,
                        "OpenCL HDR-to-SDR, e.g. hable, mobius, reinhard.",
                        &["hable", "mobius", "reinhard", "linear", "clip"],
                    ),
                    text_field(
                        "normalization.video.filters.libplacebo.tonemapping",
                        "libplacebo tonemapping",
                        true,
                        "Vulkan HDR-to-SDR, e.g. bt.2390, hable, spline.",
                        &["bt.2390", "hable", "mobius", "reinhard", "spline"],
                    ),
                ],
            ),
            group(
                "audio",
                "Audio",
                "Applies to every item that is not copied.",
                vec![
                    enum_field(
                        "normalization.audio.format",
                        "Codec",
                        &AudioFormat::OPTIONS,
                        None,
                        "Codec for transcoded items.",
                    ),
                    int_field(
                        "normalization.audio.bitrate_kbps",
                        "Bitrate (kbps)",
                        None,
                        None,
                        Some(1),
                        true,
                        "Blank lets the encoder choose. Use 384 or more for surround.",
                    ),
                    int_field(
                        "normalization.audio.buffer_kbps",
                        "Rate-control buffer (kbps)",
                        None,
                        None,
                        Some(1),
                        true,
                        "Blank lets the encoder size its own buffer.",
                    ),
                    int_field(
                        "normalization.audio.channels",
                        "Channels",
                        None,
                        None,
                        Some(1),
                        true,
                        "Speaker count, e.g. 2 or 6. Blank keeps the source layout.",
                    ),
                    int_field(
                        "normalization.audio.sample_rate_hz",
                        "Sample rate (Hz)",
                        None,
                        None,
                        Some(1),
                        true,
                        "Blank keeps the source rate. 48000 or 44100 in practice.",
                    ),
                ],
            ),
            group(
                "loudness",
                "Loudness",
                "EBU R128 levelling. Targets are LUFS and LU; a blank target uses next's default.",
                vec![
                    bool_field(
                        "normalization.audio.normalize_loudness",
                        "Normalize loudness",
                        "Level every item so the channel does not jump in volume between programs and ads.",
                    ),
                    float_field(
                        "normalization.audio.loudness.integrated_target",
                        "Integrated target",
                        true,
                        "Whole-programme target in LUFS. next defaults to -16.",
                    ),
                    float_field(
                        "normalization.audio.loudness.range_target",
                        "Range target",
                        true,
                        "Loudness range in LU. next defaults to 11.",
                    ),
                    float_field(
                        "normalization.audio.loudness.true_peak",
                        "True peak",
                        true,
                        "Ceiling in dBTP. next defaults to -1.5.",
                    ),
                ],
            ),
            group(
                "subtitle",
                "Subtitles",
                "",
                vec![
                    enum_field(
                        "normalization.subtitle.mode",
                        "Mode",
                        &SubtitleMode::OPTIONS,
                        None,
                        "Burning renders subtitles into the picture, so it counts as a transcode.",
                    ),
                    text_field(
                        "normalization.subtitle.fonts_folder",
                        "Fonts folder",
                        true,
                        "Where ffmpeg finds fonts when burning ASS/SSA subtitles.",
                        &[],
                    ),
                    text_field(
                        "normalization.subtitle.force_style",
                        "Force style",
                        true,
                        "Extra ASS style overrides, e.g. FontName=Arial,FontSize=20.",
                        &[],
                    ),
                ],
            ),
            group(
                "ffmpeg",
                "FFmpeg",
                "Blank paths use the container's ffmpeg and ffprobe from PATH.",
                vec![
                    text_field(
                        "ffmpeg.ffmpeg_path",
                        "FFmpeg path",
                        true,
                        "Only set this to pin a specific build.",
                        &[],
                    ),
                    text_field(
                        "ffmpeg.ffprobe_path",
                        "ffprobe path",
                        true,
                        "Must be the ffprobe belonging to the FFmpeg above.",
                        &[],
                    ),
                    text_field(
                        "ffmpeg.reports_folder",
                        "Reports folder",
                        true,
                        "Where ffmpeg writes reports next can read back.",
                        &[],
                    ),
                    text_list_field(
                        "ffmpeg.preferred_filters",
                        "Preferred filters",
                        "One per line. Filters to prefer when several can do the same job, best first.",
                    ),
                    text_list_field(
                        "ffmpeg.disabled_filters",
                        "Disabled filters",
                        "One per line. Filters to never select, even when they are available.",
                    ),
                ],
            ),
        ],
    })
}

fn group(id: &str, title: &str, hint: &str, fields: Vec<Value>) -> Value {
    json!({ "id": id, "title": title, "hint": hint, "fields": fields })
}

fn options(pairs: &[(&str, &str)]) -> Value {
    Value::Array(
        pairs
            .iter()
            .map(|(value, label)| json!({ "value": value, "label": label }))
            .collect(),
    )
}

/// `extra` carries the kind-specific keys — `options`, `min`, `hint` and so on.
fn field(path: &str, label: &str, kind: &str, extra: Value) -> Value {
    let mut map = match extra {
        Value::Object(map) => map,
        _ => Map::new(),
    };
    map.insert("path".to_string(), Value::String(path.to_string()));
    map.insert("label".to_string(), Value::String(label.to_string()));
    map.insert("kind".to_string(), Value::String(kind.to_string()));
    Value::Object(map)
}

fn enum_field(
    path: &str,
    label: &str,
    pairs: &[(&str, &str)],
    null_label: Option<&str>,
    hint: &str,
) -> Value {
    field(
        path,
        label,
        "enum",
        json!({
            "options": options(pairs),
            "nullable": null_label.is_some(),
            "nullLabel": null_label,
            "hint": hint,
        }),
    )
}

fn enum_list_field(path: &str, label: &str, pairs: &[(&str, &str)], hint: &str) -> Value {
    field(
        path,
        label,
        "enum_list",
        json!({ "options": options(pairs), "nullable": false, "hint": hint }),
    )
}

/// A select whose values are integers rather than strings — bit depth is the
/// one next setting that is a small fixed numeric set. Same `enum` kind and
/// same control; the option values simply keep their type so the page stores
/// `8` and not `"8"`.
fn enum_int_field(path: &str, label: &str, pairs: &[(i64, &str)], hint: &str) -> Value {
    let choices = Value::Array(
        pairs
            .iter()
            .map(|(value, label)| json!({ "value": value, "label": label }))
            .collect(),
    );
    field(
        path,
        label,
        "enum",
        json!({ "options": choices, "nullable": false, "hint": hint }),
    )
}

fn int_field(
    path: &str,
    label: &str,
    min: Option<i64>,
    max: Option<i64>,
    step: Option<i64>,
    nullable: bool,
    hint: &str,
) -> Value {
    field(
        path,
        label,
        "int",
        json!({
            "nullable": nullable,
            "hint": hint,
            "min": min,
            "max": max,
            "step": step,
        }),
    )
}

fn float_field(path: &str, label: &str, nullable: bool, hint: &str) -> Value {
    field(
        path,
        label,
        "float",
        json!({ "nullable": nullable, "hint": hint, "step": 0.1 }),
    )
}

fn text_field(path: &str, label: &str, nullable: bool, hint: &str, suggestions: &[&str]) -> Value {
    field(
        path,
        label,
        "text",
        json!({ "nullable": nullable, "hint": hint, "suggestions": suggestions }),
    )
}

fn text_list_field(path: &str, label: &str, hint: &str) -> Value {
    field(
        path,
        label,
        "text_list",
        json!({ "nullable": false, "hint": hint }),
    )
}

fn bool_field(path: &str, label: &str, hint: &str) -> Value {
    field(
        path,
        label,
        "bool",
        json!({ "nullable": false, "hint": hint }),
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::BTreeSet;

    /// next's channel schema, compiled in so a refresh that changes it fails here.
    const SCHEMA: &str = include_str!("../../../vendor/ersatztv-next/schema/channel_config.json");

    fn schema() -> Value {
        serde_json::from_str(SCHEMA).expect("next's channel schema is valid JSON")
    }

    fn resolve(reference: &str, root: &Value) -> Value {
        let path = reference
            .strip_prefix("#/")
            .unwrap_or_else(|| panic!("unsupported $ref {reference}"));
        let mut current = root;
        for segment in path.split('/') {
            current = current
                .get(segment)
                .unwrap_or_else(|| panic!("$ref {reference} does not resolve"));
        }
        current.clone()
    }

    /// Expand a schema fragment into one fully-populated instance: enums take
    /// their first value, arrays hold one element, nullable objects are present
    /// rather than null so their children can be checked.
    fn expand(def: &Value, root: &Value) -> Value {
        if let Some(reference) = def.get("$ref").and_then(Value::as_str) {
            return expand(&resolve(reference, root), root);
        }
        for key in ["anyOf", "oneOf"] {
            if let Some(branches) = def.get(key).and_then(Value::as_array) {
                let branch = branches
                    .iter()
                    .find(|branch| {
                        branch.get("type").and_then(Value::as_str) != Some("null")
                    })
                    .unwrap_or_else(|| &branches[0]);
                return expand(branch, root);
            }
        }
        if let Some(values) = def.get("enum").and_then(Value::as_array) {
            return values.first().cloned().unwrap_or(Value::Null);
        }

        let primary = match def.get("type") {
            Some(Value::String(name)) => Some(name.as_str()),
            Some(Value::Array(names)) => names
                .iter()
                .filter_map(Value::as_str)
                .find(|name| *name != "null"),
            _ => None,
        };

        match primary {
            Some("object") => {
                let mut map = Map::new();
                if let Some(properties) = def.get("properties").and_then(Value::as_object) {
                    for (name, property) in properties {
                        map.insert(name.clone(), expand(property, root));
                    }
                }
                Value::Object(map)
            }
            Some("array") => {
                let item = def
                    .get("items")
                    .map(|items| expand(items, root))
                    .unwrap_or(Value::Null);
                Value::Array(vec![item])
            }
            Some("string") => Value::String("x".to_string()),
            Some("integer") => json!(1),
            Some("number") => json!(1.0),
            Some("boolean") => json!(true),
            _ => Value::Null,
        }
    }

    /// Every leaf path in a concrete value. Arrays and scalars are leaves.
    fn leaf_paths(value: &Value, prefix: &str, out: &mut BTreeSet<String>) {
        match value {
            Value::Object(map) if !map.is_empty() => {
                for (key, child) in map {
                    let path = if prefix.is_empty() {
                        key.clone()
                    } else {
                        format!("{prefix}.{key}")
                    };
                    leaf_paths(child, &path, out);
                }
            }
            _ => {
                out.insert(prefix.to_string());
            }
        }
    }

    /// Every leaf path next's schema declares under the two keys this module
    /// owns.
    fn next_leaf_paths() -> BTreeSet<String> {
        let schema = schema();
        let mut out = BTreeSet::new();
        for key in ["FfmpegConfig", "NormalizationConfig"] {
            let def = schema
                .get("$defs")
                .and_then(|defs| defs.get(key))
                .unwrap_or_else(|| panic!("next's schema has no $defs/{key}"));
            let prefix = match key {
                "FfmpegConfig" => "ffmpeg",
                _ => "normalization",
            };
            leaf_paths(&expand(def, &schema), prefix, &mut out);
        }
        out
    }

    /// The contract: next's schema expands into a document this module can
    /// hold, and writes back out unchanged.
    #[test]
    fn holds_every_field_next_declares() {
        let schema = schema();
        let mut wanted = BTreeSet::new();
        for key in ["FfmpegConfig", "NormalizationConfig"] {
            let def = schema
                .get("$defs")
                .and_then(|defs| defs.get(key))
                .expect("schema definition");
            let prefix = if key == "FfmpegConfig" {
                "ffmpeg"
            } else {
                "normalization"
            };
            leaf_paths(&expand(def, &schema), prefix, &mut wanted);
        }

        let config: TranscodeConfig = serde_json::from_value(expand(
            &json!({
                "type": "object",
                "properties": {
                    "ffmpeg": { "$ref": "#/$defs/FfmpegConfig" },
                    "normalization": { "$ref": "#/$defs/NormalizationConfig" },
                }
            }),
            &schema,
        ))
        .expect("next's fields all fit ChannelFlow's model");

        let mut got = BTreeSet::new();
        leaf_paths(&serde_json::to_value(&config).unwrap(), "", &mut got);
        assert_eq!(wanted, got, "ChannelFlow's model and next's schema disagree");
    }

    /// The contract for the UI: the Transcode page offers exactly next's
    /// fields — no more, and none missing.
    #[test]
    fn spec_covers_every_field_next_declares() {
        let declared = next_leaf_paths();

        let spec = spec();
        let mut offered = BTreeSet::new();
        for group in spec["groups"].as_array().expect("groups") {
            for field in group["fields"].as_array().expect("fields") {
                offered.insert(field["path"].as_str().expect("path").to_string());
            }
        }

        assert_eq!(
            declared, offered,
            "the Transcode page and next's schema disagree"
        );
    }

    /// Bit depth is the one small numeric set, so the page offers it as a
    /// select — and the option values stay integers, so the stored JSON is
    /// `8`, not `"8"`.
    #[test]
    fn bit_depth_is_an_integer_choice() {
        let spec = spec();
        let field = spec["groups"]
            .as_array()
            .expect("groups")
            .iter()
            .flat_map(|group| group["fields"].as_array().expect("fields"))
            .find(|field| field["path"] == "normalization.video.bit_depth")
            .expect("a bit depth field");

        assert_eq!(field["kind"], "enum");
        let values: Vec<Value> = field["options"]
            .as_array()
            .expect("options")
            .iter()
            .map(|option| option["value"].clone())
            .collect();
        assert_eq!(values, vec![json!(8), json!(10)]);
    }

    /// A patch nests, overrides, and can force a value to null.
    #[test]
    fn overrides_merge_over_defaults() {
        let defaults = TranscodeConfig::default();
        assert_eq!(
            defaults.normalization.video.bitrate_kbps, None,
            "quality-based by default"
        );

        let merged = defaults
            .merged(&json!({
                "normalization": { "video": { "bitrate_kbps": 4000, "accel": "qsv" } }
            }))
            .expect("patch applies");

        assert_eq!(merged.normalization.video.bitrate_kbps, Some(4000));
        assert_eq!(merged.normalization.video.accel, Some(HardwareAccel::Qsv));
        // Untouched keys keep their defaults.
        assert_eq!(
            merged.normalization.video.copy_formats,
            defaults.normalization.video.copy_formats
        );

        // null is a value, not "inherit".
        let cleared = defaults
            .merged(&json!({ "normalization": { "video": { "bitrate_kbps": null } } }))
            .expect("null applies");
        assert_eq!(cleared.normalization.video.bitrate_kbps, None);
    }

    #[test]
    fn rejects_what_next_would_reject() {
        let defaults = TranscodeConfig::default();
        for bad in [
            json!({ "normalization": { "video": { "nope": 1 } } }),
            json!({ "normalization": { "video": { "bit_depth": "nine" } } }),
            json!({ "normalization": { "video": { "format": "av1" } } }),
            json!({ "normalization": { "video": { "frame_rate": "ntsc" } } }),
            json!({ "normalization": { "video": { "frame_rate": "0" } } }),
            json!({ "normalization": { "video": { "accel": "nvenc" } } }),
            json!([1, 2, 3]),
        ] {
            assert!(
                defaults.merged(&bad).is_err(),
                "{bad} should have been rejected"
            );
        }
    }
}
