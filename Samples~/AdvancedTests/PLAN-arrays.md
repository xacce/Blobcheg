# The plan of the destructive tests: arrays in a record

The scenarios and the expectations were written BEFORE the array code was read — from the contract in
`docs/blobcheg-tz-arrays.md`. The code was looked at only for the names of the types.

Everywhere the expectation is one of two: an explicit error or a deterministic result. A silent no-op,
corruption and garbage are not accepted. The rule of the set holds: reading freed memory is not
executed, it is staged so that its presence is visible — which is why "the window after End" is obliged
to be rejected by the package rather than executed by the test.

| # | Scenario | Category | Expectation |
|---|---|---|---|
| 1 | An array of a million elements | volume | it assembles, the integrity check passes, the edge elements read |
| 2 | A builder without a single Allocate — the array field is forgotten | human factor | the record reads, the array is empty, not garbage |
| 3 | A literal with an array on the second level of nesting | human factor | an explicit refusal naming the right form |
| 4 | A write into the array window after End | order | an explicit error, not a write into freed memory |
| 5 | Write threw between Begin and End | state corruption | the node's error arrives; the next rebuild is alive |
| 6 | A field of one builder in the Allocate of another | absurd | an explicit refusal, "not from this record" |
| 7 | A recursive element type, a tree two levels deep | absurd | it builds and reads level by level |
| 8 | Ten edits of the length through a rebuild | combinatorics | the file settles into a stable cycle, other addresses hold |

What is found is fixed in the package and not in the expectations; every red one gets a `// BUG:` with
the root.

## The findings of the run

A finding of the writing (before the run): the `BlobchegBuilderArray<T>` window held a raw pointer and
after `End` would have written into freed memory silently. Closed in the package: the window knows its
owner and on a closed builder it throws with the node's name.

Two findings of the run are not about arrays but about the set itself in this project:

- the router tag of the set collided with the tag of the game's `ContentRouter` (167): the tag space is
  a byte, and a set copied into a consumer's project takes part in the common handout. The package
  rejected the collision out loud, as it promised; the router of the set was renamed to `AdvAlienRouter`;
- `Nodes_with_identical_names_are_rejected_out_loud` stood on the premise "the identity of a node is the
  GUID alone", which died together with the table of name hashes: now the name is the address of the
  record in the save, and namesakes in a router are rejected by the rebuild. The expectation was
  rewritten together with the premise.
