# DIC verbose log frontend batching

DumpToolbox v0.7.90 changes only how verbose DIC audit output is presented in the Avalonia frontend.

`VERBOSE DIC` messages are placed into a concurrent queue instead of rewriting and relaying out the full TextBox for every line. A 250 ms dispatcher timer drains pending lines and performs one TextBox update for the batch. Normal progress, warning and error messages flush any pending verbose lines first and remain immediate.

No verbose records are intentionally dropped and no DIC matching/reconstruction logic is changed.
