+++
title = "Shared mutable state in Rust"
date = 2022-01-01
description = ""

[extra]
revised = 2022-01-01
keywords = "rust, shared, arc, mutex, tokio"
+++

This article explains how you can share a mutable value in Rust. For example,
the shared value could be a hash map or a counter. This is often necessary in
async applications, but we will explore how to do it in both synchronous and
asynchronous applications.

This article is focused on sharing data. If you want to share an IO resource,
please see [my article on actors][actors] instead, or if the IO resource is a
database connection, use a connection pool such as [`r2d2`][r2d2] (non-async) or
[`bb8`][bb8] (async).

[r2d2]: https://crates.io/crates/r2d2
[bb8]: https://crates.io/crates/bb8

<!-- more -->

## Sharing between multiple threads

To share a value between multiple threads, you need two things:

 1. A way to share the value. We will use an [`Arc`].
 2. A way to modify the value. We will use a [`Mutex`].

The `Arc` will allow you to share the value because whenever an `Arc` is cloned,
that gives you a new handle to the same shared value. Any changes to the value
inside will be visible in all other clones of the `Arc`. This also makes cloning
an `Arc` really cheap, since you don't actually have to duplicate the data
inside it. The data inside the `Arc` is destroyed when the last `Arc` goes out
of scope. You don't need the `Arc` if the data is shared by some other mechanism
such as a global variable.

However, an `Arc` alone provides only immutable access to the value inside it,
because it is not safe to modify a value when another thread could be reading
it at the same time. When that happens, it's called a data race.  If all you
need is to share an immutable value, that's great, but this article is about how
to modify a shared value. To do this, we add a [`Mutex`] as well. The purpose of
the `Mutex` is to ensure that only one thread can access the value at the time.
It does this using the [`lock`] method, which returns a [`MutexGuard`]. If you
call `lock` while some other thread has a `MutexGuard` from the same `Mutex`,
then the call to `lock` will sleep until that `MutexGuard` goes out of scope.
This guarantees that at most one `MutexGuard` can exist at any one time, and all
access to the inner value must go through a `MutexGuard`. It is therefore
guaranteed that no two threads can access the shared value simultaneously. This
makes it safe to modify the value.

Anyway, now that we understand the general approach, let's see an example. The
basic shape I recommend is the following:
```rust
use std::collections::HashMap;
use std::sync::{Arc, Mutex};

#[derive(Clone)]
pub struct SharedMap {
    inner: Arc<Mutex<SharedMapInner>>,
}

struct SharedMapInner {
    data: HashMap<i32, String>,
}

impl SharedMap {
    pub fn new() -> Self {
        Self {
            inner: Arc::new(Mutex::new(SharedMapInner {
                data: HashMap::new(),
            }))
        }
    }

    pub fn insert(&self, key: i32, value: String) {
        let mut lock = self.inner.lock().unwrap();
        lock.data.insert(key, value);
    }

    pub fn get(&self, key: i32) -> Option<String> {
        let lock = self.inner.lock().unwrap();
        lock.data.get(&key).cloned()
    }
}
```
This example shows how you can create a shared `HashMap`. The `SharedMap` type
derives `Clone`, but calling `clone` on it doesn't actually clone all of the
data inside it. This is due to the `Arc`. Rather, cloning it is how you can
share the value between multiple threads.

Here's an example of using it:
```rust
fn main() {
    let map = SharedMap::new();

    map.insert(10, "hello world".to_string());

    let map1 = map.clone();
    let map2 = map.clone();

    let thread1 = std::thread::spawn(move || {
        map1.insert(10, "foo bar".to_string());
    });

    let thread2 = std::thread::spawn(move || {
        let value = map2.get(10).unwrap();
        if value == "foo bar" {
            println!("Thread 1 was faster");
        } else {
            println!("Thread 2 was faster");
        }
    });

    thread1.join().unwrap();
    thread2.join().unwrap();
}
```
This example will sometimes print "Thread 1 was faster" and sometimes "Thread 2
was faster". This illustrates how the same map can be shared between several
threads.

### Why a wrapper struct?

People will often define their struct and pass around an `Arc<Mutex<MyStruct>>`
in their code, with `lock` calls all over the place. This is a bad idea for
several reasons:

 1. When reading the code, `lock` calls are unnecessary clutter.
 2. Every time a method takes the collection by argument, you have to write an
    unnecessary `Arc`/`Mutex` in the type signature. This is also clutter.
 3. You are leaking implementation details. For example, if you want to swap the
    `Mutex` for an `RwLock`, you have to change every single use of the shared
    collection across the entire codebase.
 4. You can easily hold the mutex locked for too long by accident. This is a
    particularly bad issue in async code.

Instead, I recommend that you define a wrapper struct and put the `Arc`/`Mutex`
inside it, isolating all `lock` calls to methods on the wrapper struct. This
ensures that the lock is a hidden implementation detail that the caller doesn't
have to care about.

## Asynchronous code

The technique in this post also works in asynchronous code, but you need to be
careful about one thing:

> You cannot `.await` anything while a mutex is locked.

In most cases, the compiler will give you an error if you try, but there are
some situations where the compiler doesn't catch it. In those cases, your
program will deadlock instead.

To understand why, you must understand a bit about how async/await works in
Rust. The short story is that async/await works by "swapping" out the current
task repeatedly so that it can run many tasks on a single thread. Such
"swapping" can only happen at an `.await`. The deadlock can happen because if
you `.await` while a mutex is locked, then that mutex will remain locked until
the thread swaps back to that task. If another task tries to lock the same mutex
on the same thread, then since the `lock` call is not an `.await`, it will never
be able to swap back to the other task. This is a deadlock.

In my experience, the best way to avoid the above issue is to never lock the
mutex in async code in the first place. Instead, you define non-async methods on
the wrapper struct and lock the mutex there. Then, you call those non-async
methods from your async code. Since you can't use `.await` in a non-async
method, this makes it impossible to accidentally `.await` something while the
mutex is locked.

To illustrate this, we consider an example. Imagine that you want to implement a
debouncer, which is a way to sleep until an event has not happened for some
duration of time. They are often used in user interfaces, e.g. for search bars
that automatically search once the user stops typing. To implement this, we will
create a shared variable containing an [`Instant`] for the value we want the
sleep to stop at, continually updating the shared `Instant` every time the event
happens.
```rs
use std::sync::{Arc, Mutex};
use tokio::time::{Duration, Instant};

#[derive(Clone)]
pub struct Debouncer {
    inner: Arc<Mutex<DebouncerInner>>,
}

struct DebouncerInner {
    deadline: Instant,
    duration: Duration,
}

impl Debouncer {
    pub fn new(duration: Duration) -> Self {
        Self {
            inner: Arc::new(Mutex::new(DebouncerInner {
                deadline: Instant::now() + duration,
                duration,
            })),
        }
    }

    /// Reset the deadline, increasing the duration of any calls to `sleep`.
    pub fn reset_deadline(&self) {
        let mut lock = self.inner.lock().unwrap();
        lock.deadline = Instant::now() + lock.duration;
    }

    /// Sleep until the deadline elapses.
    pub async fn sleep(&self) {
        // This uses a loop in case the deadline has been reset since the
        // sleep started, in which case the code will sleep again.
        loop {
            let deadline = self.get_deadline();
            if deadline <= Instant::now() {
                // The deadline has already elapsed. Just return.
                return;
            }
            tokio::time::sleep_until(deadline).await;
        }
    }

    fn get_deadline(&self) -> Instant {
        let lock = self.inner.lock().unwrap();
        lock.deadline
    }
}
```
With the above implementation, the `Debouncer` can be cloned and moved to many
different places, allowing one task to reset the duration that other tasks are
sleeping until, making them sleep for a longer time.

Notice how the `sleep` method in this example never actually locks the
`Mutex` even though it needs to read the data inside it. Instead, it uses the
`get_deadline` helper function. This way, we cannot accidentally forget to
unlock the mutex before the `sleep_until` call.

### When does the compiler catch the deadlock?

The compiler will usually catch the deadlock that I described earlier and give
you an error if you try to keep a mutex locked while performing an `.await`.
The error looks like this:
```
error: future cannot be sent between threads safely
   --> src/lib.rs:13:5
    |
13  |     tokio::spawn(async move {
    |     ^^^^^^^^^^^^ future created by async block is not `Send`
    |
   ::: /playground/.cargo/registry/src/github.com-1ecc6299db9ec823/tokio-0.2.21/src/task/spawn.rs:127:21
    |
127 |         T: Future + Send + 'static,
    |                     ---- required by this bound in `tokio::task::spawn::spawn`
    |
    = help: within `impl std::future::Future`, the trait `std::marker::Send` is not implemented for `std::sync::MutexGuard<'_, i32>`
note: future is not `Send` as this value is used across an await
   --> src/lib.rs:7:5
    |
4   |     let mut lock: MutexGuard<i32> = mutex.lock().unwrap();
    |         -------- has type `std::sync::MutexGuard<'_, i32>` which is not `Send`
...
7   |     do_something_async().await;
    |     ^^^^^^^^^^^^^^^^^^^^^^^^^^ await occurs here, with `mut lock` maybe used later
8   | }
    | - `mut lock` is later dropped here
```
This error happens because the `MutexGuard` type returned by `Mutex::lock` is
not safe to send across threads. You get an error because Tokio can move your
asynchronous task to a new thread whenever it has been swapped out at an
`.await`.

You only get the error if both of the following conditions are satisfied:

 1. You use the task in a way where it can be moved across threads.
 2. You are using a mutex whose `MutexGuard` does not implement `Send`.

If you are spawning your code with [`tokio::task::spawn_local`][spawn_local],
then the first condition is not satisfied and you won't get the error. The same
applies to [`block_on`][block_on] (which includes the `#[tokio::main]`
function!). Additionally, if you use a lock from an external library and that
crate's lock guard implements `Send`, then you don't get the error either. An
example of this is the [`dashmap`] crate. It is especially important to follow
the advice about never locking it in async code when using `dashmap` for this
reason.

Note that the `MutexGuard` in the `parking_lot` crate is normally not `Send`,
but the crate has a feature called `send_guard` that you can enable, which makes
all of its guards `Send`. The `dashmap` crate enables that feature, so if you
have a dependency on `dashmap`, then you have to be careful with all
`parking_lot` mutexes, because the feature is enabled globally.

[spawn_local]: https://docs.rs/tokio/latest/tokio/task/fn.spawn_local.html
[block_on]: https://docs.rs/tokio/latest/tokio/runtime/struct.Runtime.html#method.block_on

### What about the Tokio mutex?

The [`Mutex`][tokio-mutex] and [`RwLock`][tokio-rwlock] provided by Tokio is an
asynchronous lock. This means that the `lock` call itself uses an `.await`,
which allows the runtime to swap the task while its sleeping at the `lock` call.
This makes it possible to `.await` something while the lock is locked without
deadlocking the application.

You should only use an asynchronous lock if you _need_ to `.await` something
while the lock is locked. Usually, this is not necessary, and you should avoid
using an asynchronous lock when you can. Asynchronous locks are a lot slower
than blocking locks.

It is not possible to lock an asynchronous lock in the destructor of a type
because the destructor must be synchronous.

[tokio-mutex]: https://docs.rs/tokio/latest/tokio/sync/struct.Mutex.html
[tokio-rwlock]: https://docs.rs/tokio/latest/tokio/sync/struct.RwLock.html

## Alternatives to a mutex

Depending on the value you are storing and how often you need to modify it,
there are several alternatives that may be more appropriate. The most well known
of these is the [`RwLock`] type, which allows you to have multiple readers read
the value in parallel, but only one writer. For all of the alternatives
mentioned here, I still recommend that you wrap them in some kind of custom
struct.

A lesser known but very useful crate is the [`arc-swap`] crate. It is useful
when you have a rarely modified value because you can completely avoid locking.
The main difference compared to an `RwLock` is that an `RwLock` blocks all
readers for the duration of any write, whereas with `arc-swap`, you can read and
write at the same time — the existing readers continue to see the old value.
This approach typically requires cloning the entire value for every
modification, but reference counting can be used to reduce the cost of that,
possibly using the [`im`] crate. Sometimes it makes sense to combine `arc-swap`
with a mutex, where the mutex is only locked by threads that want to access the
shared value.

Another useful tool is the [`evmap`] crate (eventually consistent map), which
allows you to modify a shared hash map by having two copies of it: one for
reading and one for writing. The two maps are then swapped every so often. This
data structure is called eventually consistent because you are not guaranteed to
see your writes immediately, as they may not yet have been applied to both maps,
but you will _eventually_ see your writes if you wait for long enough.

There is also the [`dashmap`] crate which works by splitting the map into
several shards and having an `RwLock` around each shard. Accesses to different
shards are completely independent, but accesses to the same shard behave like an
ordinary `RwLock<HashMap<K, V>>`. Be aware that the lock guards used by
`dashmap` implement `Send`, so the compiler will not catch it if you keep it
locked across an `.await`. You _will_ deadlock if you don't follow the advice
about only locking it in non-async functions.

If your shared type is a cache of some kind, you could also store it in a
[thread local variable][thread_local]. This would create a separate version per
thread, and you pay for duplication of the data if multiple threads want to use
it, but it means that you can access the value without worrying about thread
safety at all.

Finally, if the data type you are sharing is an integer, you can use the
[`std::sync::atomic`] types, which let you have shared mutation without any
locking at all.

### Mutex or RwLock?

I see a tendency to always reach for the `RwLock`, and never use the `Mutex`. If
the value is read a lot more often than it is modified, then this is a good
choice, but you have to be careful with starvation:

> When lots of threads are reading from an `RwLock` all the time, a writer can
> be prevented from taking a write lock for a very long time since the number of
> readers never drops to zero. This is called starvation.

With a mutex, you are much less likely to run into issues with starvation.
Another way to avoid starvation is to use the `RwLock` in the `parking_lot`
crate. The `RwLock` in that crate uses a [fairness policy][plfair] that prevents
writers from being starved.

The asynchronous locks in Tokio are also fair.

## How do I return a reference?

You don't. References to the value inside the `Mutex` can only exist while the
mutex is locked, so returning a reference can only be done by returning the
`MutexGuard` itself. This can cause problems, e.g. if you call the method from
async code, suddenly the `MutexGuard` is no longer isolated to the non-async fn.

The easiest workaround is to simply clone the value. This is what we did in the
first example. If you want to be able to access the value as part of an async
operation, then you pretty much _have_ to do that. There are some ways to make
cloning cheaper. For example, you could use `Arc<str>` instead of `String`.

Another alternative is to use the `with_*` pattern, which looks like this:
```rs
use std::collections::HashMap;
use std::sync::{Arc, Mutex};

#[derive(Clone)]
pub struct SharedMap {
    inner: Arc<Mutex<SharedMapInner>>,
}

struct SharedMapInner {
    data: HashMap<i32, String>,
}

impl SharedMap {
    pub fn new() -> Self {
        Self {
            inner: Arc::new(Mutex::new(SharedMapInner {
                data: HashMap::new(),
            }))
        }
    }

    pub fn insert(&self, key: i32, value: String) {
        let mut lock = self.inner.lock().unwrap();
        lock.data.insert(key, value);
    }

    pub fn with_value<F, T>(&self, key: i32, func: F) -> T
    where
        F: FnOnce(Option<&str>) -> T,
    {
        let lock = self.inner.lock().unwrap();
        func(lock.data.get(&key).map(|string| string.as_str()))
    }
}
```
```rs
fn main() {
    let shared = SharedMap::new();
    shared.insert(10, "foo".to_string());

    shared.with_value(10, |value| {
        println!("The value is {:?}.", value);
    });
}
```
```text
The value is Some("foo").
```
This pattern is useful because even though it lets you run code while the mutex
is locked, calling `with_value` in async code cannot cause any of the problems
we talked about earlier. It is also useful when you don't want to define a new
method for every single operation you want to perform on your shared value.

## I want to put a `TcpStream` inside a mutex

A common mistake when writing things like chat servers is to define a collection
such as `HashMap<UserId, TcpStream>`, and to then put it inside a lock of some
kind. I think this is a big mistake, and I've never really seen it turn out
well. To handle cases like this one, I would encourage you to instead use [the
actor pattern][actors], where each `TcpStream` is exclusively owned by a spawned
task dedicated to that `TcpStream`. You can then put your actor handles in a
`HashMap`.


[actors]: /blog/actors-with-tokio/
[`Arc`]: https://doc.rust-lang.org/stable/std/sync/struct.Arc.html
[`Mutex`]: https://doc.rust-lang.org/stable/std/sync/struct.Mutex.html
[`lock`]: https://doc.rust-lang.org/stable/std/sync/struct.Mutex.html#method.lock
[`MutexGuard`]: https://doc.rust-lang.org/stable/std/sync/struct.MutexGuard.html
[`tokio::sync::Mutex`]: https://docs.rs/tokio/1/tokio/sync/struct.Mutex.html
[`std::sync::Mutex`]: https://doc.rust-lang.org/stable/std/sync/struct.Mutex.html
[blocking]: /blog/async-what-is-blocking/
[`Instant`]: https://doc.rust-lang.org/stable/std/time/struct.Instant.html
[`arc-swap`]: https://docs.rs/arc-swap/
[`im`]: https://docs.rs/im/
[`evmap`]: https://docs.rs/evmap/
[`dashmap`]: https://docs.rs/dashmap/
[`std::sync::atomic`]: https://doc.rust-lang.org/stable/std/sync/atomic/index.html
[`RwLock`]: https://doc.rust-lang.org/stable/std/sync/struct.RwLock.html
[thread_local]: https://doc.rust-lang.org/std/macro.thread_local.html
[plfair]: https://docs.rs/parking_lot/0.11/parking_lot/type.RwLock.html#fairness
