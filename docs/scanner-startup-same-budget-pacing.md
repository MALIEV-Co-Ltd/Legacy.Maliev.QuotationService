# Original scanner same-budget readiness pacing

Actual e6e3 readiness cap refusal was independently observed after16 settled,
accounted connect refusals and zero PONGs; backend delay cause remains unknown.
Prior1e PONG16 is separate evidence. The same16 attempts are now spaced over
the unchanged absolute readiness budget120/180 seconds, retaining <=3s calls.
All original relay/history/settlement/quarantine/source caps remain unchanged.
There is no attempt17. After16 refusals only remaining absolute budget is waited
before deadline-expired refusal; early returned waits recheck the original
slot target and deadline. Late PONG is refused before the original seal. Late wake never extends the deadline or launches
after it. PONG returns immediately to original handoff seal and all controls.

Dedicated actor-free pacing controls run in the already held startup suite.
They do not qualify actual source timing, kernel resources, readiness or cleanup.
Storage classifier is a separate unpublished proposal, with no changes here.
Actual fresh scanner/readiness/helper/held/full/security proof remains required.
