# Issue #87 PR design notes

Recorded before implementation, based on main
`58b7cf47a98c21aee52b23666ab59c3b6ca72375` and ADR-028.

- Names use the existing EnvironmentId validator unchanged: lowercase
  kebab-case, leading letter, at most 64 characters. Metadata contains only
  the logical name and separate persisted UUIDv7 EnvironmentRecordId.
- Create-or-get has Created and Existing outcomes with identical metadata on
  retries. PostgreSQL's existing unique name index arbitrates concurrent
  inserts. INSERT ON CONFLICT DO NOTHING followed by a fresh read returns
  the committed winner. The compatibility ISecretStore helper delegates to
  this same catalog algorithm. No schema migration or secret write is needed.
- Get returns Found, NotFound or InvalidName. Create returns InvalidName for
  invalid external names; validation never normalizes or echoes invalid input.
- List uses ascending ordinal name order (PostgreSQL COLLATE "C"), with
  keyset continuation strictly after the last name. Default page size is 50;
  explicit sizes must be 1..100 (wire value 0 selects the default).
  Fetch at most size + 1 rows; emit a next cursor exactly when another row
  exists. Return no implicit truncation. An empty final page is successful.
- Cursor v1 is the bounded ASCII token `env1:<last-name>`. Validate the
  version, total length (at most 69) and name using EnvironmentId before any
  storage call; malformed tokens return InvalidCursor. It is a continuation
  position, not a credential: a valid name need not currently exist.
  Page size may change between requests. Invalid sizes return InvalidPageSize.
  Ordering and cursors are independent of database locale and process state.
- Pagination is a live traversal, not a snapshot: newly inserted names after
  the cursor can appear; names inserted before it require a new traversal.
  There is no rename or delete operation in this slice.
- Alpha gRPC exposes CreateEnvironment, GetEnvironment and ListEnvironments
  with typed status values and metadata only. Server application use cases
  validate names and cursors. CLI commands are `environment create <name>`,
  `environment get <name>` and `environment list --page-size N --cursor TOKEN`.
  List renders one page and always displays its continuation, including JSON.
  Local contexts select the registry only; no context read creates state.
- CLI output uses explicit source-generated JSON DTOs and existing exit codes:
  0 success, 1 expected rejection, 2 usage/configuration, 3 transport failure.
  No Provider bootstrap, secrets, provisioning, auth or local config writes.
