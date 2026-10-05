# Analytics hosted-worker source proof

Source `2bb747212f8c722115e53691e10a522d8a793cf7` introduced the scoped continuous GA outbox worker; `119e41973e888895da191fbf17635ca649b05a1e` suppresses its registration only for the exact standby value `true`. The accepted service retains those runtime semantics. Existing registration and directly invoked processor tests do not exercise the actual host-owned delivery loop.

Six new cases start the normal Production Program, registered worker, real PostgreSQL store and unchanged typed-client pipeline. A controlled external primary transport returns 204, 400 or exhausted 500; two queued intents and batch size one prove repeated scope/batch delivery, durable sent/failed acknowledgement and no replay on later polls. Standby and disabled delivery retain an unclaimed due intent. Disposal cancels an in-flight provider send without failure logs or acknowledgement, retaining the actual lease for later recovery.

Only test configuration uses synthetic credentials, one-second polling and a fixed clock. No worker, processor, SDK pin, provider settings, auth, production feature default or deployment change. Hosted build/full suite, native reports, raw owned coverage and static gates are required before acceptance. This is bounded worker/standby evidence, not closure of every path in either source commit.
