//! Alan adı listesi (GoodbyeDPI'nin `--blacklist` karşılığı).
//!
//! Kural: satırdaki değer ana makine adının kendisi ya da üst alan adı ise eşleşir.
//! `x.com` girdisi `x.com` ve `api.x.com` ile eşleşir, `notx.com` ile eşleşmez.

#[derive(Default)]
pub struct HostList {
    hosts: Vec<String>,
}

impl HostList {
    pub fn is_empty(&self) -> bool {
        self.hosts.is_empty()
    }

    pub fn len(&self) -> usize {
        self.hosts.len()
    }

    /// Dosyadan okur. Bulunamazsa boş liste döner (çağıran karar verir).
    pub fn load(path: &str) -> std::io::Result<HostList> {
        let text = std::fs::read_to_string(path)?;
        Ok(Self::parse(&text))
    }

    pub fn parse(text: &str) -> HostList {
        let mut hosts = Vec::new();
        for raw in text.lines() {
            let line = raw.trim();
            if line.is_empty() || line.starts_with('#') {
                continue;
            }
            // Yorumları satır içinde de kes
            let line = line.split('#').next().unwrap_or(line).trim();
            let line = line.trim_start_matches("*.").trim_end_matches('.');
            if line.len() < 3 {
                continue;
            }
            let mut h = line.to_ascii_lowercase();
            // "host:port" biçimini tolere et
            if let Some((name, port)) = h.rsplit_once(':') {
                if port.chars().all(|c| c.is_ascii_digit()) && !name.is_empty() {
                    h = name.to_string();
                }
            }
            hosts.push(h);
        }
        HostList { hosts }
    }

    /// Ana makine listede mi? (alt alan adları dahil)
    pub fn matches(&self, host: &str) -> bool {
        let h = host.to_ascii_lowercase();
        let h = h.split(':').next().unwrap_or(&h);
        let h = h.trim_end_matches('.');
        self.hosts.iter().any(|p| {
            h == p.as_str() || (h.len() > p.len() && h.ends_with(p.as_str()) && h.as_bytes()[h.len() - p.len() - 1] == b'.')
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_and_matches() {
        let list = HostList::parse("discord.com\n# yorum\n\n  *.instagram.com  \nX.COM\n");
        assert_eq!(list.len(), 3);
        assert!(list.matches("discord.com"));
        assert!(list.matches("cdn.discord.com"));
        assert!(list.matches("www.instagram.com"));
        assert!(list.matches("x.com"));
        assert!(list.matches("API.X.com"));
    }

    #[test]
    fn does_not_over_match() {
        let list = HostList::parse("x.com\n");
        assert!(!list.matches("notx.com"));
        assert!(!list.matches("ax.com"));
        assert!(!list.matches("x.com.evil.net"));
        assert!(!list.matches("google.com"));
    }

    #[test]
    fn ignores_junk_lines() {
        let list = HostList::parse("ab\n#\n\n  \nreal.example\n");
        assert_eq!(list.len(), 1);
        assert!(list.matches("real.example"));
    }

    #[test]
    fn strips_port_and_trailing_dot() {
        let list = HostList::parse("example.com:443\nfoo.net.\n");
        assert!(list.matches("example.com"));
        assert!(list.matches("foo.net"));
    }

    #[test]
    fn empty_list_matches_nothing() {
        let list = HostList::parse("");
        assert!(list.is_empty());
        assert!(!list.matches("discord.com"));
    }
}
