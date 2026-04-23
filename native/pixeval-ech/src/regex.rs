use std::hash::{Hash, Hasher};

use regex::Regex;

#[derive(Clone)]
pub struct RegexKey {
    pub pattern: String,
    pub regex: Regex,
}

impl PartialEq for RegexKey {
    fn eq(&self, other: &Self) -> bool {
        self.pattern == other.pattern
    }
}
impl Eq for RegexKey {}

impl Hash for RegexKey {
    fn hash<H: Hasher>(&self, state: &mut H) {
        self.pattern.hash(state);
    }
}
