# Legacy distribution recovery audit

This audit records exactly which original DSLT distribution assets are
preserved and prevents an unavailable legacy runtime or sample image from being
silently treated as validation evidence. The machine-readable source is
[`legacy-distribution.lock.json`](../validation/legacy-distribution.lock.json),
and `verify-legacy-distribution.ps1` checks it without network access.

## Original distribution inventory

The legacy root `README.md` names twelve externally hosted assets:

- four installers: x64 and x86 builds of versions 1.11 and 1.00;
- two manuals: versions 1.11 and 1.00;
- four TIFF files representing two acquisitions and their x86 variants; and
- two LSM files representing those same two acquisitions.

The historical GitHub owner URL `nslab2000/DSLT` now resolves through the
GitHub API to `takashi310/DSLT`. Neither the current tree nor any object reachable
from baseline commit `aae2b3e5310fcaad4151a878ad65ed2a3fa29146` contains the
named installers or microscopy files. They were linked from the separate
`dslt.bot.kyoto-u.ac.jp` host.

## Recovery result on 2026-08-04

The historical host no longer resolved in DNS. An Internet Archive CDX query
over 2013 through 2026 returned one unique successful URL: the version 1.11
manual. No installer, TIFF, or LSM URL appeared in that result.

The archived manual replay is a 1,048,576-byte prefix and explicitly reports
that it was truncated by the crawler. That prefix is byte-identical to the
first 1 MiB of the preserved 2,192,662-byte publisher Appendix S1:

| Evidence | SHA-256 |
|---|---|
| Full publisher manual | `aa2c21a6fe3aafc7a431d117aed4ede66b6878f807304805d73ffea602392014` |
| Wayback replay and publisher-file prefix | `a617b85cc56dbffd8b94c2395e7cff9b632c5adddd7ae6e54e3b26f87234b89e` |

Common Crawl index requests returned HTTP 504 during this audit. That route is
therefore recorded as indeterminate, not as proof that no Common Crawl capture
exists.

## Validation consequence

The preserved full manual is authoritative documentation evidence, but it is
not an executable oracle. No original installer, sample TIFF, sample LSM, or
accepted legacy segmentation output is currently available. Consequently:

- public real LSM decoding now has exact independent comparison evidence, but
  the two original DSLT sample LSMs remain unavailable for lineage-specific comparison;
- legacy-runtime output comparison remains pending;
- the two named acquisitions contribute zero cases to the five-acquisition
  v1.0 metric gate; and
- source-level contracts and synthetic oracles must remain clearly separated
  from runtime-equivalence claims.

If an asset is recovered later, do not overwrite this conclusion directly.
First record its custody, original URL or archive record, byte size, SHA-256,
and acquisition identity in a new lock revision; then run malware scanning and
static inspection before executing any installer.
