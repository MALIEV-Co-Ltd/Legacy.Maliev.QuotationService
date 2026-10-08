# Fixture-corrected source qualification

The prior 75-file capsule built in Release with zero warnings and errors, but its complete service suite returned 1159 passed and 191 failed out of 1350, with zero skips (run 37698536970). The preserved failures identify stale authentication fixtures, a two-job validator applied to a three-job workflow, and two persistence oracles.

This new scope retains every prior source file and adds the workload-token lifecycle fixture required to correct the distinct login profiles and caches. Eight existing files change; the complete capsule has 76 files. The default full-candidate 75-file and admission-race exact-three-file scopes remain unchanged. Trusted manifest, inventory, raw source and dependency hashes continue to bind every file. Unknown or mismatched scope and dropped/added entries fail closed.

The main-only hosted workflow uses the existing trusted native recipe: original full suites, focused inventory, warnings as errors, format, complete package graph plus vulnerability audit, unexcluded raw and handwritten coverage floors and final byte readback. It retains the original 4096 MiB admission guard and worker/cache checks. The frozen 76-file source is uncommitted C#; transport acceptance does not qualify the application or prove live IAM or eight-host financial behavior.
