# 0008. No re-hosting of community fixes; behaviour specs, not ported code

- **Status:** accepted.
- **Context:** several popular mods (NRaas, simler90, MonoPatcher, S3SS) have no licence file, so all rights stay with their authors.
- **Decision:** the installer sets community fixes up from their official sources and never re-hosts them. Unlicensed mods are read for behaviour only: write a behaviour spec in your own words, implement from the spec, credit the author, never port line by line. Where we fix a bug in another mod we send the author a small PR and ship our own fix with a stand-down.
- **Consequences:** we ship only our own fixes (`ts3fixes.package`, `ts3native`); compatibility is handled by stand-down rules and a scan, not by bundling.
- **Sources:** README.md ("Community fixes are not part of the product", "Respect other authors"); CONTRIBUTING.md; AGENTS.md "Community code" and "Bugs in other mods".
