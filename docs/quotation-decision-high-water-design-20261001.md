# Quotation #92: decision generation across ordinary edits

## Ownership and bounded contract

Initially TEST/DESIGN ONLY for https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.QuotationService/issues/92, on `codex/quotation-decision-high-water-20261001` at protected main `1869cb8984741e9869e2f8b5074af0a004ea8d19`. After executed RED and root review, ownership additionally includes the bounded ordinary-update high-water repair in `QuotationRepositories.cs`. All 517 accepted tests, schema, CI, permissions and consumer implementations remain unchanged. Root-owned untracked Python cache directories and ignored #88/#89 proof artifacts are preserved.

Private dependencies remain Defaults `8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3`, Contracts `78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7`, and test-only Accounting `ae0826156b06c34476e95de8c53dfccfcf5a5972`. Outputs use this worktree's `.dependencies`, never sibling outputs.

## Executed RED and compatibility controls

Actual normal Production Program HTTP uses the unchanged `InvoiceConsumerFixture`: ordinary RS256 employee authentication, disposable PostgreSQL 18 and Redis, registered actual pinned IAM client with controlled remote transport, and actual Order decision client/service-token provider with controlled remote Order responses. IAM resource `global` is the existing compatibility setting, not proof of activated resource-scoped or external live IAM authority. Only TimeProvider is replaced for deterministic clock control; no repository/auth substitute is used.

Both collision cases execute initial accept, decline, ordinary PUT retaining declined state, reaccept, then repeat decline/ordinary PUT/reaccept. Every request obtains fresh HTTP CAS; each mutation is independently read from fresh PostgreSQL. Before key uniqueness fails, assertions prove five successful Order calls, final accepted state, NULL invoice and binding, one unchanged immutable acceptance outcome, original acceptance time/origin and unchanged request/Journey provenance.

At microsecond-aligned `T = 2031-02-03T04:05:06Z` (ticks `640634547060000000`):

| Clock during ordinary edit | Persisted decline/edit ticks | Accepted key versions | Result |
| --- | --- | --- | --- |
| T | T+20 / T in both cycles | T+10, T+10, T+10 | Genuine RED: expected 3 distinct, actual 1 |
| T-50 ticks | first T+20 / T-50; second T-30 / T-50 | T+10, T-40, T-40 | Genuine RED: expected 3 distinct, actual 2 |

The first RED emits `quotation-3-accepted-8e3fdb9f05ac50a-order-701` three times. The backward-clock RED emits `quotation-2-accepted-8e3fdb9f05ac50a-order-701`, then `quotation-2-accepted-8e3fdb9f05ac4d8-order-701` twice. These are synthetic fixture identifiers, not customer data.

Five controls PASS: unchanged acceptance replay at fixed/backward clock preserves version/key/outcome; historical accepted ordinary edit freezes its prior binding despite backward clock; stale ordinary PUT is 409 with unchanged fields/version/state and subsequent fresh decision distinct; stale changed decision is 409 without an additional Order call or outcome change.

## Cause and proposed policy boundary

`QuotationRepositories.ApplyDecisionAsync` resets DecisionOrderVersion for a normal changed decision and computes `NextDecisionVersion(now, previous ModifiedDate)`. The helper is correctly monotonic and PostgreSQL-microsecond aligned relative to that current persisted value. `UpdateQuotationAsync` binds old accepted/unbound versions before Map, preserving #88, but ordinary ModifiedDate remains raw `Now()`. After decline has reset the binding, an ordinary edit can lower the sole reference used by the next decision. The workflow's accepted key uses DecisionOrderVersion, otherwise ModifiedDate/CreatedDate; therefore the next fresh acceptance can reuse an earlier generation's key. #89's first-decision precision repair remains valid and is not a global monotonic guarantee.

Root approved the narrow no-schema policy: ordinary quotation updates now calculate `NextDecisionVersion(Now(), entity.ModifiedDate)` before Map or Save, preserving the existing pre-Map historical accepted binding, then assign the computed value after Map. Ordinary ModifiedDate is PostgreSQL-microsecond aligned and can lead wall-clock time under stalled/backward clocks. This intentionally changes ordinary-write version timestamp semantics; it is not merely precision normalization. Raw AcceptedUtc/outcome event timestamps are unchanged. Exhaustion fails before Map/binding/Save with an opaque HTTP500. Child/file/request timestamps, employee authority, schema and immutable acceptance events are not broadened. A new independent durable generation column was not needed or added.

Producer key collision is now executed proof. Actual Order receipt/deduplication effects remain unproven by the controlled transport and need separate consumer acceptance; do not claim a suppressed or incorrect real Order transition. No source parity retirement or parent #68 closure follows from this test.

## Commands and terminal evidence

All commands run sequentially in the owned worktree, using `-p:MalievWorkspaceRoot=B:/maliev-legacy/.worktrees/quotation-invoice-consumer-proof-20261001/.dependencies`.

- Baseline and new-test builds: `dotnet build Legacy.Maliev.QuotationService.slnx -c Release -p:UseSharedCompilation=false` with the property above; both terminal 0 warnings / 0 errors. Logs `TestResults/decision92-baseline-build.log` and `TestResults/decision92-test-build.log`.
- Unchanged focus: `dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~QuotationFirstDecisionPrecisionHttpTests|FullyQualifiedName~QuotationInvoiceConsumerContractHttpTests' --logger trx --results-directory TestResults/decision92-baseline-focus` plus the property: 32 PASS, 0 fail, 0 skip. TRX `natth_MALIEV-31USFIV_2026-10-01_18_46_40_net10.0.trx`.
- New focus: same test command with `--filter FullyQualifiedName~QuotationDecisionHighWaterHttpTests --results-directory TestResults/decision92-red`: 7 executed, 5 PASS, 2 intended genuine RED, 0 skip. TRX `natth_MALIEV-31USFIV_2026-10-01_18_49_47_net10.0.trx`, SHA256 `D73DDF7FA51EA7B8D801943AE9C8A1E4C7E2EFD1F0327E48EF4C7EC5D63ADCBF`.

This deliberately RED design slice is not merge-ready. The full accepted 517 suite was not rerun at this design gate; it is root's prior accepted baseline, not a new full-green claim. No runtime/provider/persistent-data/deployment/GitHub changes or commits occurred.

Scoped `dotnet format Legacy.Maliev.QuotationService.slnx --verify-no-changes --no-restore --include Legacy.Maliev.QuotationService.Tests/Controllers/QuotationDecisionHighWaterHttpTests.cs` with the same private root passed terminal exit 0. `git diff --check` passed; `gitleaks dir` separately scanned each new file with `--no-banner --redact`, with no leaks found. No build/test/format handle remains. The only new owned files are the test and this document; root's two Python cache directories remain untouched.

The preceding design-gate status is historical, retained as RED chronology. Before the repair, three additional cases were added and executed: fixed/backward-clock ordinary edits must advance by one microsecond per write without creating acceptance events, and maximum version must produce opaque500 with no payload/binding/outcome/Order effect. The resulting 10-case run was 5 PASS / 5 genuine RED / 0 skip, `TestResults/decision92-extra-red/natth_MALIEV-31USFIV_2026-10-01_18_59_15_net10.0.trx`; exhaustion actually returned204. Its build was0W0E (`decision92-extra-red-build.log`). The repair then built0W0E (`decision92-final-build.log`). Final acceptance evidence follows below once terminal; the earlier RED suite is not described as green.

## Final local acceptance — frozen for independent root review

- Final Release build above: 0 warnings / 0 errors, exit0.
- Focus with all three HTTP classes (`QuotationDecisionHighWaterHttpTests`, `QuotationFirstDecisionPrecisionHttpTests`, `QuotationInvoiceConsumerContractHttpTests`): 42 PASS / 0 fail / 0 skip, terminal exit0. TRX `TestResults/decision92-final-focus/natth_MALIEV-31USFIV_2026-10-01_19_00_07_net10.0.trx`.
- Full command `dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj -c Release --no-build --no-restore -p:MalievWorkspaceRoot=B:/maliev-legacy/.worktrees/quotation-invoice-consumer-proof-20261001/.dependencies --collect 'XPlat Code Coverage' --logger trx --results-directory TestResults/decision92-final-full`: 527 PASS / 0 fail / 0 skip, exit0, duration2m40s. Includes all unchanged517 accepted tests plus new10. TRX `natth_MALIEV-31USFIV_2026-10-01_19_02_01_net10.0.trx`, SHA256 `136E78B007CDF98616E2749DFE62F2976763024881B51CFE915D1E3CA8B4D6CF`.
- Unexcluded report `TestResults/decision92-final-full/b5889c83-8937-4bf4-a198-e57721d52847/coverage.cobertura.xml`, SHA256 `D570F1560053187ED9DE9FF27BD61173668070312A6E15767F4EC7E6B9CF90FF`. Exact unchanged `python -B scripts/check_owned_coverage.py <that-report> --minimum 80`: PASS, owned handwritten5202/5486=94.82%, API432/469=92.11%. Raw report retains generated and external dependencies; they are separately printed and not counted as handwritten Quotation coverage.
- `python -B -m unittest discover -s scripts/tests -p 'test_*.py' -v`: all5 PASS. Its intentional synthetic failing-coverage diagnostic is expected proof, not a failing test. `-B` prevents creation of additional Python caches.
- Whole `dotnet format Legacy.Maliev.QuotationService.slnx --verify-no-changes --no-restore` with the same owned root environment: exit0 (`decision92-final-format.log`). `actionlint` and `git diff --check`: exit0.
- Six sequential `dotnet list <Api/Application/Data/Domain/MigrationRunner/Tests.csproj> package --vulnerable --include-transitive --no-restore`: each exit0 / no vulnerable packages, `TestResults/decision92-audit-<project>.log`.
- Pinned Workflows `73dd7304ffe85ec504389fd7664cc39070b9f148:scripts/JwtSigningResourceScanner.ps1` loaded with read-only `git show`; `Test-JwtSigningResourceMaterial` over tracked paths plus new test/doc returned false (no signing material). Scoped `gitleaks dir <file> --no-banner --redact` on runtime/test/doc: exit0 / no leaks.

All build/test/format/audit handles are terminal and output ownership is released to root. Candidate is exactly the two-line ordinary-update high-water change, the new10-case test file and this document. No old tests or configuration changed. Local GREEN is not protected-CI/merge/deployment acceptance, external live IAM provisioning, actual Order deduplication acceptance, parent #68 completion or a global timestamp-policy guarantee. No commit/push or external mutation occurred.

## Historical authority extension — new RED, awaiting root runtime review

The preceding 527-case GREEN is retained as historical evidence, not final acceptance of the extended candidate. Root's read-only review identified a future historical key above current ModifiedDate. Six new cases were added without further runtime edits. The first run `decision92-historical-red/...19_13_29...trx` was 10 PASS / 6 failures during new fixture seeding: UTC DateTime values cannot be written to the existing timestamp-without-time-zone columns. It is an authoring diagnostic, not product RED. Only the new seeded dates were changed to Unspecified Kind; a fresh Release build again completed0W0E (`decision92-historical-red2-build.log`).

Corrected actual HTTP focus: `TestResults/decision92-historical-red2/natth_MALIEV-31USFIV_2026-10-01_19_14_32_net10.0.trx`, 16 executed / 11 PASS / 5 genuine key-collision RED / 0 skip. All five REDs reached their final distinct-key assertion after successful HTTP transitions and fresh persisted state/binding/reset/single-original-outcome assertions. Here T is the fixed clock and offsets are .NET ticks (10 ticks = one microsecond):

| Prior accepted key authority | Ordinary edit? | Prior accepted H | Decline version | Reaccept version |
| --- | --- | --- | --- | --- |
| ModifiedDate NULL, CreatedDate H | no | T+10 | T | T+10 = H |
| ModifiedDate NULL, CreatedDate H | yes | T+20 | T+10 | T+20 = H |
| ModifiedDate T, DecisionOrderVersion H | no | T+20 | T+10 | T+20 = H |
| ModifiedDate T, DecisionOrderVersion H | yes | T+30 | T+20 | T+30 = H |
| ModifiedDate T, binding H; ordinary Map Accepted=false | yes | T+20 | no explicit decline | T+20 = H |

The sixth new control PASS proves a future CreatedDate is NOT authority when ModifiedDate is nonnull and no binding exists: initial keyT, ordinary versionT+10, declineT+20, acceptanceT+30 despite CreatedDateT+one year. All earlier10 cases continue PASS.

Proposed scoped invariant for root review: before Map/reset, calculate the version floor as max(current ModifiedDate, authoritative retained DecisionOrderVersion or existing ModifiedDate/CreatedDate/epoch fallback). A retained binding must not be lost merely because ordinary Map previously set Accepted=false. Future CreatedDate must not override a nonnull ModifiedDate/binding. Apply that floor in both ordinary UpdateQuotationAsync and changed ApplyDecisionAsync, retaining early unchanged replay, historical capture, normal binding resets and raw event times. No implementation of this extension has occurred; no schema/keybuilder/old-test changes are proposed. Current test candidate is deliberately RED and frozen awaiting root review.

## Approved historical floor repair

Root reviewed the literal historical REDs and approved `DecisionVersionFloor(Quotation)` exactly as `max(entity.ModifiedDate, entity.DecisionOrderVersion ?? entity.ModifiedDate ?? entity.CreatedDate ?? UnixEpoch)`. Both paths now compute NextDecisionVersion against that floor before any Map, binding/reset or late-attachment mutation. This carries existing key authority into a new generation without changing which version the unchanged replay/keybuilder uses. A retained binding remains relevant after ordinary Accepted=false mapping; future CreatedDate stays ignored when ModifiedDate or binding already supplies authority. Raw `now` remains the acceptance event timestamp. No schema/keybuilder/old tests changed.

Two additional retained-binding maximum-version controls were built0W0E and executed before repair: ordinary update returned204 and changed decline200 instead of opaque500, 2 genuine RED / 0 PASS / 0 skip, `TestResults/decision92-binding-exhaust-red/natth_MALIEV-31USFIV_2026-10-01_19_17_48_net10.0.trx`. They require fresh unchanged ModifiedDate/binding/payload/accepted/event/outcome and no Order call once the floor is exhausted. Fresh repaired build0W0E is `decision92-final2-build.log`; final test count is now517+18=535. Earlier527 GREEN and historical16 RED remain chronology, not the latest acceptance claim. Latest focused/full/static results follow after terminal execution.

## Latest complete local acceptance — 535 cases

- Fresh Release0W0E, then focus50 PASS / 0 fail / 0 skip, `TestResults/decision92-final2-focus/natth_MALIEV-31USFIV_2026-10-01_19_18_46_net10.0.trx`.
- Same full command recorded above, with `--results-directory TestResults/decision92-final2-full`: all535 PASS / 0 fail / 0 skip, exit0, duration2m33s. All517 old tests remain unchanged. TRX `natth_MALIEV-31USFIV_2026-10-01_19_20_42_net10.0.trx`, SHA256 `56DB347FBC45EE010D3089221BBE64A002C35AFFA98AED7B88AC12F83B1D5EFA`.
- Exact unexcluded report `TestResults/decision92-final2-full/6f94e798-d5c1-4f13-9ab9-315cf3e2c16d/coverage.cobertura.xml`, SHA256 `CF1F917463ED8E7113DB2CA1ED85EF3611E5A15C7915AA10CF69A6F634155295`. Unchanged `python -B scripts/check_owned_coverage.py <that-report> --minimum 80`: owned handwritten5206/5490=94.83%, API432/469=92.11%, PASS. Raw generated/external lines remain visible and excluded only by the unchanged handwritten checker rules.
- Whole-solution format exit0 (`decision92-final2-format.log`); same five checker proof tests PASS; actionlint and diffcheck exit0. Six fresh sequential transitive package audits exit0/no vulnerable packages (`decision92-final2-audit-<project>.log`). Pinned73dd signing-resource scan, scoped runtime/test/doc secret scans and final whitespace readback passed.

All owned handles are terminal. Freeze scope is three files: QuotationRepositories.cs (two call sites plus private floor), new18-case HTTP test file and this evidence document. Root's Python cache directories remain untouched. No provider/persistent-data changes, migration/schema changes, activation, auth/keybuilder/old-test edits or consumer changes occurred. This proves the bounded producer generation invariant under recorded ordinary/historical clock cases, not global timestamps or actual downstream Order deduplication behavior.

## Independent root final acceptance

Root read the complete final runtime and18-case fixture, then independently ran Release build0warnings/0errors, focus50PASS/0skip and full535PASS/0fail/0skip in2m38s against the private exact-pinned graph. TRX `TestResults/root-decision92-final-full/root-decision92-final-full.trx`. Raw unexcluded coverage `TestResults/root-decision92-final-full/1764ca63-3af6-40bd-9f63-41bdb1f22512/coverage.cobertura.xml`, SHA256 `340862165D8A013E2C443B352480F2D6DBEDD7BFC02E54E4F934B5EC709D1988`. Unchanged checker: owned5208/5490=94.86%, API434/469=92.54%; no threshold or exclusion change.

Root whole format, unchanged five coverage-checker proof tests, actionlint, six transitive dependency audits and whitespace checks passed. The checker prints a below-threshold message for its intentional synthetic negative fixture; all five checker tests pass. An initial unsupported `dotnet format --property` invocation failed before formatting; the corrected existing environment-property invocation passed without file changes. Only the three owned paths may be staged; unrelated Python caches and ignored proof outputs stay untouched. Protected head/post-merge CI and main-tree verification remain required; parent68 and source-owner acceptance stay open.
