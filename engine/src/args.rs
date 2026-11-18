use std::path::PathBuf;

#[derive(Debug)]
pub struct Config {
    pub domains: PathBuf,
    pub http: bool,
    pub tls_reorder: bool,
}

impl Config {
    pub fn parse(args: Vec<String>) -> Result<Self, String> {
        let mut domains = None;
        let mut http = false;
        let mut tls_reorder = false;
        let mut args = args.into_iter();
        while let Some(arg) = args.next() {
            match arg.as_str() {
                "--domains" if domains.is_none() => {
                    let value = args.next().filter(|value| !value.starts_with("--"))
                        .ok_or("--domains requires a file path")?;
                    domains = Some(PathBuf::from(value));
                }
                "--http" => http = true,
                "--tls-reorder" => tls_reorder = true,
                _ => return Err(format!("Unknown or duplicate option: {arg}")),
            }
        }
        Ok(Self { domains: domains.ok_or("--domains is required")?, http, tls_reorder })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_native_options() {
        let args = ["--domains", "site list.txt", "--http"].map(String::from).to_vec();
        let config = Config::parse(args).unwrap();
        assert_eq!(config.domains, PathBuf::from("site list.txt"));
        assert!(config.http);
        assert!(!config.tls_reorder);
    }

    #[test]
    fn tls_reordering_is_opt_in_and_does_not_enable_http() {
        let args = ["--domains", "sites.txt", "--tls-reorder"].map(String::from).to_vec();
        let config = Config::parse(args).unwrap();
        assert!(config.tls_reorder);
        assert!(!config.http);
    }

    #[test]
    fn requires_domain_list_and_rejects_unknown_options() {
        assert!(Config::parse(vec![]).is_err());
        assert!(Config::parse(vec!["-9".into()]).is_err());
        assert!(Config::parse(vec!["--domains".into(), "--http".into()]).is_err());
    }
}
