# DIC tab split

The DiscImageCreator recovery workflow is now isolated from the Redumper resurrection workflow.

## Resurrect

The Resurrect tab is Redumper-only again:

- Redumper `.skeleton`
- matching `.hash`
- recursive/non-recursive SHA-1 source scanning
- persistent `.dumptoolbox_hashcache.json`
- optional Force rehash
- fast block-based resurrection
- Allow missing files defaults to off

No DiscImageCreator controls or matching rules are used by this tab.

## DIC

The new `DIC` tab contains the DiscImageCreator recovery workflow:

- choose any DIC companion log; matching `*_volDesc.txt`, `*_disc.txt`, `*_mainInfo.txt` and `*EccEdc.txt` files are discovered automatically
- build a synthetic 2352-byte raw skeleton
- preserve DIC Mode 1 / Mode 2 Form 1 / Mode 2 Form 2 sector layout and XA subheaders where logged
- use DIC filesystem extents and timestamps
- source matching always searches all subfolders and ignores the recovery folder hierarchy
- strict case-insensitive primary ISO9660 relative-path + filename + exact-byte-size matching
- rebuild using the same fast sequential raw-sector engine
- Allow missing files defaults to off

The DIC and Redumper UI state, progress, tree and cancellation controls are independent.
