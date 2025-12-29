+++
title = "RFC: Target Modifiers"
date = 2025-02-13
description = "A new RFC for target modifiers in Rust."

[extra]
keywords = "rust, rfc, target, modifiers"
revised = 2025-02-13
redirect_to = "https://rust-lang.github.io/rfcs/3716-target-modifiers.html"
+++

This RFC introduces the concept of "target modifiers," flags that can cause unsoundness if compilation units disagree on them. We propose failing the build if `rustc` detects disjoint target modifier sets between units, with an escape hatch (`-Cunsafe-allow-abi-mismatch`) available. This RFC does not stabilize any specific modifiers but establishes the framework for handling them.

<!-- more -->

You can read the full RFC on the Rust RFCs website.
