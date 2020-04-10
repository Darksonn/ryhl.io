+++
title = "Backblaze B2 Rust library"
date = 2017-06-27
description = "This page functions as a home page for my rust library backblaze-b2-rs, which is used for interacting with the web service Backblaze B2."

[extra]
revised = 2020-04-05
keywords = "backblaze, b2, backup, library, rust"
+++

This page functions as a home page for my rust library `backblaze-b2`. Here are some
links to the project:

 * [crates.io](https://crates.io/crates/backblaze-b2)
 * [Documentation](https://docs.rs/backblaze-b2)
 * [Git repository](https://github.com/Darksonn/backblaze-b2-rs)

<!-- more -->

To add this library as a dependency to your Rust crate, add the following line to your
`Cargo.toml`:

```toml
[dependencies]
backblaze-b2 = "0.1.9-2"
```

The library is used for interacting with the web service [Backblaze B2][b2-docs].
Backblaze is a company that provides a backup service, and they have created a web
service with an api called B2. This service allows the user to store files on their
servers, allowing them to be retrieved at any time.

The service functions through an https json interface, and this library has various
functions for listing, uploading and downloading files on Backblaze B2 through this api.

The library is currently under development to be updated for async await.

[b2-docs]: https://www.backblaze.com/b2/docs/
[rs-docs]: https://docs.rs/backblaze-b2/
