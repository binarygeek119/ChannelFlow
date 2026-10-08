//! Version matching between a plugin and the base it runs on.

use semver::Version;

/// Parse a version string into something comparable.
pub fn parse(value: &str) -> Result<Version, semver::Error> {
    Version::parse(value.trim().trim_start_matches('v'))
}

/// A base of `base` can run a plugin whose range is `min..=max`. An empty
/// bound means "no lower (or upper) limit".
pub fn compatible(base: &str, min: &str, max: &str) -> bool {
    let base = match parse(base) {
        Ok(base) => base,
        Err(_) => return false,
    };
    if !min.trim().is_empty() {
        if let Ok(min) = parse(min) {
            if base < min {
                return false;
            }
        }
    }
    if !max.trim().is_empty() {
        if let Ok(max) = parse(max) {
            if base > max {
                return false;
            }
        }
    }
    true
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn matches_ranges() {
        assert!(compatible("2.0.0", "2.0.0", "2.999.999"));
        assert!(!compatible("3.0.0", "2.0.0", "2.999.999"));
        assert!(compatible("1.9.0", "", "1.999.999"));
        assert!(compatible("2.0.0", "2.0.0", ""));
        assert!(compatible("2.10.0", "2.9.0", "2.11.0"), "10 > 9 numerically");
    }
}