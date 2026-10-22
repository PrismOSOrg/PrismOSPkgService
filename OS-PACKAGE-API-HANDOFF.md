# PrismOS package API handoff

This document describes the complete package-repository API available to an OS
client: browsing and downloading packages, registering and signing in, managing
favorites, uploading releases, viewing download statistics, and editing a
release description. The API is implemented by this repository; OS-side client
and UI code belongs in the PrismOS project.

## Connection and authentication

The API base path is `/api/v1`. Configure the server origin for the environment;
for local development it is `http://10.0.2.2:8080` from a QEMU guest and
`http://127.0.0.1:8080` on the host. The service currently uses plain HTTP and
is intended only for development and testing.

Authenticated requests use:

```http
Authorization: Bearer <accessToken>
```

Tokens are opaque, expire after 12 hours, and are revoked by logout. Send the
token on authenticated account operations, uploads, and chunk downloads that
should be attributed to the signed-in user. Catalog and manifest reads are
public. Chunk reads are also public without a token; if a token is supplied,
it must be valid.

## Account lifecycle

`POST /auth/register` creates an account and signs it in. `POST /auth/login`
signs in an existing account. Both accept JSON:

```json
{"username":"prism-user","password":"a-long-password"}
```

Usernames are trimmed and normalized to lowercase. They must be 3–24 ASCII
letters, digits, dots, underscores, or hyphens, and begin with a letter or
digit. Passwords must be 12–128 characters.

Registration returns `201 Created`; login returns `200 OK`. Both return:

```json
{
  "accessToken": "<opaque-token>",
  "tokenType": "Bearer",
  "username": "prism-user",
  "expiresIn": 43200
}
```

Invalid registration data and duplicate usernames return `400` with an `error`
message. Invalid login credentials return `401`. Authentication requests are
rate-limited to 10 per IP per minute; handle `429 Too Many Requests` by waiting
before retrying.

- `GET /auth/me` returns `{"username":"prism-user"}` for the current token.
- `POST /auth/logout` revokes the current token and returns `204 No Content`.
- Missing, expired, revoked, or malformed credentials on protected routes
  return `401 Unauthorized`.

Persist the token only in the OS's appropriate private user storage. Clear it
after logout or when the server returns `401`, then ask the user to sign in
again. Never log passwords or bearer tokens.

## Browse and download packages

All package identifiers and versions are 1–32 ASCII letters, digits, dots,
underscores, or hyphens, beginning with a letter or digit. IDs are canonicalized
to lowercase; versions are case-sensitive.

### Catalog

`GET /catalog` returns UTF-8 text:

```text
PRISMPKG-CATALOG/1
<id>\t<version>\t<size-bytes>\t<lowercase-sha256>\t<manifest-path>
```

Rows are sorted by ID and version. The default catalog cap is 6144 bytes; the
server refuses to start if the configured catalog limit is exceeded. The OS
HTTP client should read the complete response, require the exact format header,
validate every field, and reject malformed rows or duplicate package-version
keys.

### Manifest

`GET /packages/{id}/{version}/manifest` returns UTF-8 text:

```text
PRISMPKG-MANIFEST/1
id\t<id>
version\t<version>
description\t<description>
size\t<size-bytes>
sha256\t<lowercase-sha256>
chunk-size\t<configured-chunk-size>
chunk-path\t<chunk-endpoint-path>
uploaded-by\t<username-or-repository>
```

Descriptions are at most 120 characters; tabs and line breaks are replaced
with spaces. The OS should check that the manifest ID and version match the
catalog entry, and that its size and SHA-256 agree with the catalog.

### Chunk download

`GET /packages/{id}/{version}/chunk?offset=N&length=M` returns binary bytes.
`offset` is a nonnegative zero-based byte offset. `length` must be between 1
and the manifest's `chunk-size`; the configured maximum cannot exceed 4096
bytes. The final chunk can be shorter than requested.

Download by requesting consecutive chunks, appending each response to a
temporary file, and checking the received length against the package size.
Reject early EOF, extra bytes, unexpected status codes, and any final size
mismatch. Compute SHA-256 incrementally and compare it with the manifest before
installing. A matching digest detects corruption, not a malicious or
unauthorized publisher.

Common responses: `200` success, `400` missing or invalid offset/length,
`401` invalid supplied bearer token, `404` unknown package version, and `416`
offset at or beyond the package size. Do not use HTTP `Range`; this endpoint
uses query parameters.

## Register, log in, and manage favorites

Favorites belong to the signed-in account and identify a specific package
version:

- `GET /users/me/favorites` returns an array of `{id, version, description}`.
- `PUT /users/me/favorites/{id}/{version}` adds a favorite; the package version
  must exist. Returns `204 No Content`.
- `DELETE /users/me/favorites/{id}/{version}` removes a favorite. Returns
  `204 No Content`, including when it was not already present.

All three require a bearer token. Adding an unknown package version returns
`404`; missing or invalid authentication returns `401`. Favorites for package
versions that are no longer present are omitted from the returned list.

## Upload a release

`POST /uploads` publishes a new package version. It requires a bearer token and
the raw package file as the request body (not multipart form data):

```http
POST /api/v1/uploads
Authorization: Bearer <accessToken>
Content-Type: application/octet-stream
Content-Length: <number-of-bytes>
X-Package-Id: my-package
X-Package-Version: 1.2.0
X-Package-Description: Optional short description

<raw package bytes>
```

`X-Package-Description` is optional and limited to 120 characters. Include an
accurate `Content-Length`; the server streams and hashes the body, and rejects
a mismatch. The default maximum upload is 32 MiB per package and 512 MiB for
the repository. A successful upload is indexed immediately; no restart is
needed. Existing package versions cannot be overwritten.

Success returns `201 Created` with:

```json
{"id":"my-package","version":"1.2.0","size":12345,"sha256":"<lowercase-sha256>"}
```

Common errors: `400` invalid metadata, empty body, or length mismatch; `401`
missing/invalid token; `409` version already exists; `413` upload too large;
`415` content type is not `application/octet-stream`; `507` repository or
catalog limit reached. Any registered user can upload; there is no review,
admin role, or package-delete endpoint.

## Releases, metadata, and download statistics

- `GET /users/me/releases` lists the current user's releases with `id`,
  `version`, `description`, `size`, and `sha256`.
- `PUT /users/me/releases/{id}/{version}` changes only the description of a
  release owned by the current user. Send
  `{"description":"New description"}`; the maximum is 120 characters. Success
  returns the updated release data. Missing/non-owned releases return `404`;
  missing or overlong descriptions return `400`.
- `GET /users/me/downloads` returns per-user download statistics.
- `GET /stats/downloads` returns public aggregate statistics by package.

Each statistics row contains `id`, `version`, `downloadStarts`, `bytesServed`,
and `lastRequestedUtc`. A chunk request starting at offset zero counts as a
download start; `bytesServed` counts bytes sent by the server, not bytes
confirmed by the client. Only chunk requests with a valid bearer token are
attributed to that user.

## Other useful endpoints

- `GET /healthz` returns plain-text `ok`.
- `GET /swagger` is the browser-based API reference.
- `GET /swagger/v1/swagger.json` returns the OpenAPI document.

The website routes `/`, `/catalog`, and `/account` are browser UI, not OS API
contracts. No endpoint in this service supports password reset, email
verification, account deletion, package deletion, publisher authorization,
TLS, or package-signature verification.

## OS client requirements and trust

The existing OS-side HTTP client has historically supported only synchronous
plain-HTTP GET requests into bounded buffers. To implement this complete API,
the OS networking layer needs request headers and JSON bodies, POST/PUT/DELETE,
response status/header handling, and streamed request/response bodies. Avoid
buffering a full package in memory; uploads and downloads can be up to the
configured package limit.

The server is development/test infrastructure. It has no TLS, MFA, email
verification, production-grade account recovery, or package signature checks.
Do not send credentials over an untrusted network. Validate package format and
publisher trust in the OS before installation or execution. SHA-256 is
integrity metadata only and does not authenticate the source.
