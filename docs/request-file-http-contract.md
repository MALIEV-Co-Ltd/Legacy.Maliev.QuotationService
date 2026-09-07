# Request-file HTTP response contract

`QuotationRequestFileHttpContractTests` protects the producer response consumed by
the Web issue #195 companion change. It sends an HTTP POST through ASP.NET Core
TestServer, MVC model binding, the actual `QuotationRequestFilesController`, named
route generation, and the JSON output formatter.

For `POST /quotationrequests/417/files`, a created file with ID 23 returns:

- HTTP 201 and JSON content type.
- An **absolute** `Location`, such as
  `https://quotation.example.test/quotationrequests/files/23`.
- PascalCase `Id`, `RequestId`, `Bucket`, and `ObjectName` properties; null
  `CreatedDate` and `ModifiedDate` properties are omitted.

Consumers must not reject this response merely because `Location` is absolute.
The fixture uses a synthetic HTTPS origin; it does not assert a deployed hostname
or reverse-proxy configuration.

## Deliberate scope

The test host mirrors the JSON options in `Program.cs`; it does not execute the
production startup pipeline. Changes to production JSON configuration must keep
this fixture aligned. The repository/service and idempotency boundaries are mocked.
The request omits `Idempotency-Key` and asserts no idempotency-store calls.

Endpoints explicitly allow anonymous access **only in this fixture**. The named
permission policy is registered so MVC can resolve its metadata, not to validate
authorization. This test provides no evidence for JWT, live IAM, authorization,
database persistence, Redis replay, GCS access, or deployed end-to-end behavior.
Those concerns require their separate tests; production protection is unchanged.
