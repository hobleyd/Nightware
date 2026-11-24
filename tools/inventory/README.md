# tools/inventory

These scripts keep `docs/contract/endpoints.json` honest against the NightMail
client. They use only the Python 3 standard library.

## check_inventory.py

```bash
python3 tools/inventory/check_inventory.py            # scan ~/Source/nightmail working tree
python3 tools/inventory/check_inventory.py --rev HEAD # scan a committed revision instead
python3 tools/inventory/check_inventory.py --client /path/to/nightmail --list
```

The client defaults to `$NIGHTMAIL_SRC`, then `~/Source/nightmail`. `--list`
prints every extracted call as `file:line method version path -> operation(s)`.

The script exits non-zero if any of these is true:

- a Graph request path in the client is not covered by an operation in
  `endpoints.json`;
- an operation has no fixture, or a fixture names no operation;
- any JSON file is invalid, or an operation lacks a required key.

**How paths are found.**

- The script takes single-quoted Dart string literals from:
  - `lib/data/datasources/remote/graph_*.dart`;
  - any file whose constructor takes `required GraphHttpClient client`.
- From every other file under `lib/`, it takes only absolute
  `https://graph.microsoft.com/(v1.0|beta)/…` literals, and literals that start
  with an interpolated Graph root (`$graphBaseUrl`, `${…GraphBetaBaseUrl}`,
  `$_betaBase`).
- OAuth scope strings are ignored.

**How paths are normalised.**

- `$_base` and `$mailboxBase` become `{mailbox}`.
- Every other interpolation becomes a wildcard segment.
- `?…` is dropped.
- Adjacent Dart literals split over two lines (`'/drives/$id/items/'` then
  `'$itemId'`) are joined, as Dart joins them.
- A placeholder inside an operation's segment (`search(q='{q}')`) matches
  anything there, including the client's own interpolation.
- A path held in a named constant is versioned by the root it is joined to.
  For example, `'$_betaBase$_threatSubmissionPath'` makes the constant's path
  beta.

**How methods are found.** The HTTP method is read from the nearest
`_dio.get/post/patch/put/delete(` call. `_fetchAllGraphPages`, `collect` and
`_eventIdByNavigation` count as GET. A path whose method can't be found matches
any method.

**What counts as covered.** A `{mailbox}` path must be covered for **both** the
`me` and `users` forms, because the client's `_base` can be either. An
`external` operation, such as the upload-session PUT, is never matched against
client paths.

The two known false-positive shapes are skipped:

- a line that only builds or tests the mailbox base (`_base = … ? '/me' :
  '/users/$x'`, `startsWith('/users/')`);
- the threat-submission `messageUrl` body value, listed in `NOT_A_CALL`.

When the client gains a new Graph call, add an operation to `endpoints.json`,
add its fixture, then run `render_endpoints_md.py`.

## render_endpoints_md.py

```bash
python3 tools/inventory/render_endpoints_md.py
```

This rewrites the generated operation tables in `docs/contract/endpoints.md`,
between the `BEGIN GENERATED` and `END GENERATED` markers, from
`endpoints.json`. The hand-written sections around them are left alone.
