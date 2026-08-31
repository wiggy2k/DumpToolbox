# Resurrect performance design

The active resurrection path no longer copies the skeleton and then patches it sector-by-sector.

For raw 2352-byte skeletons it now processes 2048 sectors (~4.8 MiB) at a time:

1. Read one large block sequentially from the skeleton.
2. Insert all matched user-data bytes that belong in that block.
3. Mark only affected sectors as dirty.
4. Rebuild EDC/ECC for dirty sectors in parallel.
5. Write the completed block once to the `.partial` output.

For cooked 2048-byte skeletons, unchanged skeleton ranges and recovered source extents are streamed directly into the new output in LBA order.

This removes the previous full skeleton copy plus hundreds of thousands of tiny asynchronous random reads/writes.
