+++
title = "RFC: #[derive(SmartPointer)]"
date = 2024-07-11
description = "A new RFC for deriving SmartPointer in Rust."

[extra]
keywords = "rust, rfc, smartpointer, derive"
revised = 2024-07-11
redirect_to = "https://rust-lang.github.io/rfcs/3621-derive-smart-pointer.html"
+++

Make it possible to define custom smart pointers that work with trait objects.
For now, it will only be possible to do this using a derive macro, as we do not
stabilize the underlying traits.

<!-- more -->

You can read the full RFC on the Rust RFCs website.
