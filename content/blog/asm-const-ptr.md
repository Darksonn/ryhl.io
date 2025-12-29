+++
title = "RFC: Pass pointers to const in assembly"
date = 2025-11-05
description = "A new RFC for passing pointers to const in inline assembly."

[extra]
keywords = "rust, rfc, inline assembly, const, pointers"
revised = 2025-11-05
redirect_to = "https://rust-lang.github.io/rfcs/3848-asm-const-ptr.html"
+++

The `const` operand to `asm!` and `global_asm!` currently only accepts
integers. Change it to also accept pointer values. The value must be computed
during const evaluation. The operand expands to the name of the symbol that the
pointer references, plus an integer offset when necessary.

<!-- more -->

You can read the full RFC on the Rust RFCs website.