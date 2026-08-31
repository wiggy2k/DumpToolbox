# DIC v0.7.39 — Joliet identifier form and geometry

Joliet reconstruction now treats the supplementary file-version suffix and inherited primary System Use as independent evidence choices.

Candidate order for an SVD-proven translated/paired layout:

1. historical `name;1` + inherited primary System Use;
2. unversioned Joliet `name` + inherited primary System Use;
3. unversioned Joliet `name` without inherited primary System Use;
4. historical `name;1` without inherited primary System Use.

The first candidate whose calculated directory lengths fit the independently proven SVD geometry without overlap is selected. Existing byte-exact layouts therefore keep their previously proven representation, while a geometrically impossible representation cannot force the supplementary tree to be abandoned.

The selected identifier form is used both when calculating sector packing and when writing directory records.
