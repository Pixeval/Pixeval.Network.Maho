use std::fmt::Write;

pub fn format_error(err: &dyn std::error::Error) -> String {
    let mut s = format!("{}", err);

    let mut current = err;
    while let Some(src) = current.source() {
        let _ = write!(s, "\n\nCaused by: {}", src);
        current = src;
    }
    s
}

pub fn to_owned_error(err: &dyn std::error::Error) -> Box<dyn std::error::Error + Send + Sync> {
    format_error(err).into()
}