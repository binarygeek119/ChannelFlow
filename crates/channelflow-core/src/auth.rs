//! The web UI's credentials and session.
//!
//! Setup creates one admin account (salted hash — basic but fine for a
//! self-hosted single-user box) and marks an `setup_complete` flag. After that
//! every API call except the auth endpoints requires a session cookie. The
//! cookie holds only the token; the token itself is kept in the store, where
//! `logout` can revoke it.

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};

/// What setup stores. The password is never kept — only a salted hash.
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct AuthRecord {
    pub username: String,
    pub salt: String,
    pub password_hash: String,
    pub setup_complete: bool,
}

/// Hash `password` against `salt`, in the `sha256$iterations$hex` shape.
pub fn hash_password(salt: &str, password: &str) -> String {
    let mut hasher = Sha256::new();
    hasher.update(salt.as_bytes());
    hasher.update(password.as_bytes());
    let digest = hasher.finalize();
    let hex: String = digest.iter().map(|byte| format!("{byte:02x}")).collect();
    format!("sha256$1${hex}")
}

/// Constant-time record/expected comparison so the password is not revealed by
/// timing.
pub fn verify(record: &AuthRecord, password: &str) -> bool {
    constant_time_eq(
        hash_password(&record.salt, password).as_bytes(),
        record.password_hash.as_bytes(),
    )
}

/// Constant-time comparison of two session tokens.
pub fn verify_token(left: &str, right: &str) -> bool {
    constant_time_eq(left.as_bytes(), right.as_bytes())
}

/// A new random salt and a new random session token.
pub fn new_secret() -> String {
    uuid::Uuid::new_v4().to_string()
}

fn constant_time_eq(left: &[u8], right: &[u8]) -> bool {
    if left.len() != right.len() {
        return false;
    }
    let mut diff = 0u8;
    for (a, b) in left.iter().zip(right.iter()) {
        diff |= a ^ b;
    }
    diff == 0
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hashes_and_verifies() {
        let salt = new_secret();
        let record = AuthRecord {
            username: "admin".to_string(),
            salt: salt.clone(),
            password_hash: hash_password(&salt, "hunter2"),
            setup_complete: true,
        };
        assert!(verify(&record, "hunter2"));
        assert!(!verify(&record, "wrong"));
        assert!(constant_time_eq(b"abc", b"abc"));
        assert!(!constant_time_eq(b"abc", b"abd"));
    }
}