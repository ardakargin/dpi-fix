use std::path::PathBuf;

#[derive(Debug)]
pub struct Config {
    pub domains: PathBuf,
    pub http: bool,
    pub tls_reorder: bool,
    pub tls_record_split: bool,
}

impl Config {
    pub fn parse(args: Vec<String>) -> Result<Self, String> {
        let mut domains = None;
        let mut http = false;
        let mut tls_reorder = false;
        let mut tls_record_split = false;
        let mut args = args.into_iter();
        while let Some(arg) = args.next() {
            match arg.as_str() {
                "--domains" if domains.is_none() => {
                    let value = args.next().filter(|value| !value.starts_with("--"))
                        .ok_or("--domains requires a file path")?;
                    domains = Some(PathBuf::from(value));
                }
                "--http" => http = true,
                "--tls-reorder" if !tls_reorder => tls_reorder = true,
                "--tls-record-split" if !tls_record_split => tls_record_split = true,
                _ => return Err(format!("Unknown or duplicate option: {arg}")),
            }
        }
        if tls_reorder && tls_record_split {
            return Err("--tls-reorder and --tls-record-split are mutually exclusive".into());
        }
        Ok(Self { domains: domains.ok_or("--domains is required")?, http, tls_reorder, tls_record_split })
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
        assert!(!config.tls_record_split);
    }

    #[test]
    fn tls_record_split_is_a_separate_profile() {
        let args = ["--domains", "sites.txt", "--tls-record-split"].map(String::from).to_vec();
        let config = Config::parse(args.clone()).unwrap();
        assert!(config.tls_record_split);
        assert!(!config.http);
        assert!(!config.tls_reorder);
        let mut conflicting = args;
        conflicting.push("--tls-reorder".into());
        assert!(Config::parse(conflicting).is_err());
    }

    #[test]
    fn requires_domain_list_and_rejects_unknown_options() {
        assert!(Config::parse(vec![]).is_err());
        assert!(Config::parse(vec!["-9".into()]).is_err());
        assert!(Config::parse(vec!["--domains".into(), "--http".into()]).is_err());
    }
}
