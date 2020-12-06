+++
title = "A mental model for asynchronous Rust"
date = 2020-04-10
description = "The purpose of this post is to give you a good mental model for understanding asynchronous Rust, and to give you an overview of the toolbox available to you in the surrounding ecosystem. I will be focusing on the Tokio runtime, as I believe it is the most promising option, but most of the things here should apply to all runtimes."

[extra]
revised = 2020-04-10
keywords = "rust, async, await, Tokio, guide"
+++

The purpose of this post is to give you a mental model for understanding asynchronous
Rust, and to give you an overview of the toolbox available to you in the surrounding
ecosystem. I will be focusing on the [Tokio] runtime, as I believe it is the most
promising option. That said, most of the things here apply to all runtimes.

[Tokio]: https://tokio.rs/

<!-- more -->

The main reason asynchronous Rust exists is so you can do many things simultaneously,
without having to spawn a thread for every single thing. By pausing a task in the middle,
you can alternate between many tasks, effectively letting you run all of them
simultaneously. In Rust, these tasks are implemented by creating a value with a `poll`
function, which is used to continue executing the task. The standard library provides a
trait called [`Future`][future] for this purpose.  Additionally, Rust provides a special
syntax for creating futures, which turns imperative code into a future whose execution
can be paused. It looks like this:
```rs
use tokio::time::{delay_for, Duration};

#[tokio::main]
async fn main() {
    println!("Hello World!");

    delay_for(Duration::from_secs(5)).await;
//                                   ^ execution can be paused here

    println!("Five seconds later...");
}
```
There is often a natural place to pause execution. A good example of this is when a task
is waiting for a timer. Rust takes advantage of this by letting futures decide by
themselves when it is a good time to pause: They return from the call to the `poll`
method, when the future reaches a good spot to sleep. Notice that this requires the
futures to cooperate with each other, since tasks that spend a long time inside `poll`
will starve the other tasks. When a future returns from `poll` and gives control back to
the executor, that is called yielding. You may have come across the phrase "You should
not block in async code". In this context, the phrase "to block" means that a future is
not yielding control for an extended period of time, starving other tasks.

[future]: https://doc.rust-lang.org/stable/std/future/trait.Future.html

To tell whether you are blocking inside async code, you should look at where you use
`.await`. This is because an async function can only yield back to the executor when it
reaches an `.await`. There is no guarantee that a future yields at every `.await` — the
thing you await decides — but it can only happen at an `.await`. A common example where
this approach reveals blocking is using the [`sleep`] function from the standard library:
```rs
use std::thread::sleep;
use std::time::Duration;

#[tokio::main]
async fn main() {
    println!("Hello World!");

    // No .await here!
    sleep(Duration::from_secs(5));

    println!("Five seconds later...");
}
```
This appears to work fine, but that is because it is the only running task. As soon as
you start doing that in several tasks, your program will very quickly grind to a halt due
to the starvation of other tasks.

[`sleep`]: https://doc.rust-lang.org/stable/std/thread/fn.sleep.html

## Notifications

The part of your program that decides which futures to poll is called the executor. A
critical part of deciding which futures to poll is knowing which of them are still
waiting, and which are ready to continue. For example, if a task is currently waiting for
a timer, it doesn't make sense to poll it right now. In Rust, this works through a
notification system. Every time a future is polled, it is given a [`Waker`], and when
that future is ready to continue work, it should call the `wake` function on that waker.
This highlights an important point: Futures must have some sort of external piece of code
to wake themselves up.

In some cases, adding such an external component is not difficult. For example in the
case of [channels], the sender can wake the receiver up, when it sends a message.
Unfortunately it is not always that easy. One way to implement timers is to spawn a
thread and emit the wake-up from the new thread, but this is very inefficient. Because of
this, executors such as Tokio provide timer utilities that integrate directly with that
executor. This means that when no tasks on the executor are ready to continue, it will
go to sleep for the duration of the smallest running timer, and when the executor wakes
back up, the executor itself will send the notification to the task with that timer.

This technique where timers are integrated into the event loop allows for very efficient
timer implementations. Unfortunately it means that you cannot use that timer interface
with other executors, because the notification component is missing. The same applies to
IO primitives such as networking: Tokio uses a special api provided by the OS, which
allows listening for events from many IO resources simultaneously. This makes IO much
more efficient, but ties you to that runtime.

[`Waker`]: https://doc.rust-lang.org/stable/std/task/struct.Waker.html

## State machines

It is not obvious how the compiler converts the snippet below into a future. The future
has to somehow remember how far into the function it got, since it must be possible to
pause execution at any `.await`.
```rs
use tokio::time::{delay_for, Duration};

#[tokio::main]
async fn main() {
    println!("Hello World!");

    delay_for(Duration::from_secs(5)).await;

    println!("Five seconds later...");
}
```
A good way to think about this is that the compiler automatically generates an enum with
a variant for every `.await` in your function, plus one at the start and one at the end.
This kind of construction is known as a state machine. The example above would be
converted into something similar to this:
```rs
use tokio::time::{delay_for, Delay, Duration};

fn main() -> MainFuture {
    MainFuture::NotStarted()
}

enum MainFuture {
    NotStarted(),
    Waiting(Delay), // type of the Future returned by delay_for
    Done(),
}
// This is simplified and does not compile!
impl Future for MainFuture {
  type Output = ();
  fn poll(&mut self) -> Poll<()> {
    loop {
      match self {
        MainFuture::NotStarted() => {
          println!("Hello world!");
          let delay = delay_for(Duration::from_secs(5));
          *self = MainFuture::Waiting(delay);
          // Do another iteration in the loop.
        },
        MainFuture::Waiting(delay) => {
          if delay.poll().is_ready() {
            println!("Five seconds later...");
            *self = MainFuture::Done();
            // Tell Tokio that we are done.
            return Poll::Ready(());
          } else {
            // Yielding and going to sleep. zzz
            return Poll::Pending;
          }
        },
        MainFuture::Done() => {
          panic!("Poll on finished Future.");
        },
      }
    }
  }
}
```
The implementation of `poll` you see above definitely does not compile, and glosses over
a bunch of details such as notifications and pinning. A thorough explanation of how you
can manually implement the `Future` trait is beyond the scope of this post, but I hope
the example gives you an idea of what the compiler does when it sees an async function.
If you want to read more about this, I strongly recommend [this blog post][osphilopp].

[osphilopp]: https://os.phil-opp.com/async-await/

Despite the simplifications, we can explore several important features of async functions
using the example above. The first thing I wish to explore is that if a future is not
polled, no work gets done. This is quite clear in our example:
```rust
fn main() -> MainFuture {
    MainFuture::NotStarted()
}
```
This function does not do anything on its own. If you simply call it and throw away the
return value, nothing would have happened; not even the `println!("Hello World!")` before
the first await gets executed. Luckily the compiler will emit a warning when futures are
created and immediately destroyed to ensure you don't forget to `.await` them.

Another feature is that an async function might not yield at all when it reaches an
`.await`. This can be seen in the example by noticing the loop around the match. If the
awaited future says that it is done the first time you poll it, the async function will
not yield control back to the executor. It will simply move on to the next state, and go
around the loop one more time. Similarly, a future can also yield many times at a single
await. If you poll our future ten times while it is waiting for the timer, it will yield
at the same `.await` each time.

### Async blocks

Async blocks are also very useful, and are similar to closures in a lot of ways. They
allow you to easily create future objects inside other functions, that you can then pass
as arguments to functions that take futures as an argument.
```rs
use tokio::time::{delay_for, Duration};

#[tokio::main]
async fn main() {
    let a = tokio::spawn(async {
        delay_for(Duration::from_secs(2)).await;
        println!("After two seconds...");
    });

    let b = tokio::spawn(async {
        delay_for(Duration::from_secs(3)).await;
        println!("After one more second...");
    });

    // Wait for both tasks. Order here does not matter.
    b.await.unwrap();
    a.await.unwrap();
    println!("Done!");
}
```
Here we use the [`tokio::spawn`][spawn] function, which takes a future, and runs it as an
independent Tokio task. It returns a [`JoinHandle`], which you can later `.await` to wait
for the task to finish. Tokio will catch panics inside the spawned tasks, and the
`unwrap()` will fail if one of them panics.

[spawn]: https://docs.rs/tokio/0.2/tokio/fn.spawn.html
[`JoinHandle`]: https://docs.rs/tokio/0.2/tokio/task/struct.JoinHandle.html

Async block's most important feature is that they can capture their environment. This
allows you to either borrow or give ownership of local variables to the async block. When
the compiler turns async blocks into enums, as we saw previously, those values from the
environment become fields in that enum. For a thorough explanation of how the environment
is captured, I recommend [this blog post][closures], which explains the analogous concept
for closures.

[closures]: https://krishnasannasi.github.io/rust/syntactic/sugar/2019/01/17/Closures-Magic-Functions.html

The most important consequence of captured variables is, that if the environment is only
borrowed, you cannot spawn the async block. This is because `tokio::spawn` requires that
the future is `'static`, which means that it is not allowed to contain any references.
When you run into this issue, you have three options:

1. Give away ownership.
2. Use an [`Arc`] to share the value.
3. Don't spawn it.

[`Arc`]: https://doc.rust-lang.org/stable/std/sync/struct.Arc.html

In most cases, it is best to give away ownership, because values that are owned by a
single task are much simpler to reason about than values that are shared. One exception
to this is immutable data, where using an `Arc` makes a lot of sense. A common pitfall
I run into a lot is trying to share network connections or other IO resources in an
`Arc<Mutex<...>>`. This is basically never a good idea; spawning a task for each
connection and using [channels] often results in much simpler code.

[channels]: https://docs.rs/tokio/0.2/tokio/sync/mpsc/index.html

The third option is also important to keep in mind. There are many tools you can use to
run several things simultaneously without spawning tasks. For example, Tokio provides the
[`join!`], [`try_join!`] and [`select!`] macros, and the futures crate provides
[`join_all`] and [`FuturesUnordered`]. I wont be going through all of these, so I
encourage you to read their documentation, but you can use them to rewrite our previous
example:

[`join!`]: https://docs.rs/tokio/0.2/tokio/macro.join.html
[`try_join!`]: https://docs.rs/tokio/0.2/tokio/macro.try_join.html
[`select!`]: https://docs.rs/tokio/0.2/tokio/macro.select.html
[`join_all`]: https://docs.rs/futures/0.3/futures/future/fn.join_all.html
[`FuturesUnordered`]: https://docs.rs/futures/0.3/futures/stream/struct.FuturesUnordered.html

```rs
use tokio::time::{delay_for, Duration};

#[tokio::main]
async fn main() {
    // No tokio::spawn here.
    let a = async {
        delay_for(Duration::from_secs(2)).await;
        println!("After two seconds...");
    };

    let b = async {
        delay_for(Duration::from_secs(3)).await;
        println!("After one more second...");
    };

    // Neither a nor b has started running yet.
    // At this point, they're just a variable.

    // Run both async blocks simultaneously in one task.
    tokio::join!(a, b);
    println!("Done!");
}
```
If you use the standard library's `sleep` function in the example above, it will take
five seconds to run instead of just three. This is because `join!` is implemented by
polling each future once whenever the containing future is polled, so if one of the
futures spend a long time inside that poll, the other future is not polled until
afterwards.

### Example: Capturing your environment

The server module in [the Hyper crate][hyper] has a rather convoluted api, and requires
the largest number of nested closures and async blocks I know of anywhere in Rust. This
makes it an amazing example.

[hyper]: https://docs.rs/hyper

The basic idea behind the api is this: When an http server receives a connection, that
connection might receive several http requests. To handle this, hyper wants you to
provide two levels of handlers. The outer handler is called for every connection, and it
should return a handler, which is then called for each request on that connection. To
explain these features, I will be going through the example below:
```rs
use std::convert::Infallible;
use std::net::SocketAddr;
use std::sync::Arc;

use hyper::server::conn::AddrStream;
use hyper::service::{make_service_fn, service_fn};
use hyper::{Server, Request, Response, Body};

type BoxError = Box<dyn std::error::Error + Send + Sync>;

struct SharedData {
    foo: u32,
}

#[tokio::main]
async fn main() {
    let addr = SocketAddr::from(([127, 0, 0, 1], 8100));

    let shared = Arc::new(SharedData {
        foo: 10,
    });

    // This is the connection handler.
    let make_service = make_service_fn(move |client: &AddrStream| {
        let ip = client.remote_addr();
        let shared = shared.clone();
        async move {
            // This is the request handler.
            Ok::<_, Infallible>(service_fn(move |req| {
                let shared = shared.clone();
                handle_request(ip, req, shared)
            }))
        }
    });
    let server = Server::bind(&addr).serve(make_service);

    println!("Starting server on http://localhost:8100/");
    if let Err(e) = server.await {
        eprintln!("server error: {}", e);
    }
}

async fn handle_request(
    ip: SocketAddr,
    req: Request<Body>,
    shared: Arc<SharedData>,
) -> Result<Response<Body>, BoxError> {
    Ok(Response::new(Body::from(
        format!(
            "Hi {}. You want {} and foo is {}.",
            ip,
            req.uri(),
            shared.foo,
        )
    )))
}
```
This example has been adapted from [one of Hyper's examples][http_proxy]. The main focus
of this example will be the journey of `shared` as it moves through the layers of
closures and async blocks. Let's first look at the request handler:

[http_proxy]: https://github.com/hyperium/hyper/blob/master/examples/http_proxy.rs

```rs
// This is the request handler.
Ok::<_, Infallible>(service_fn(move |req| {
//                             ^ give closure ownership of shared
    let shared = shared.clone();
//               ^ make a clone to give to handle_request
    handle_request(ip, req, shared)
}))
```
Since `handle_request` is an async function, the function call you see above doesn't do
anything. It just creates a future, which the closure immediately returns. The request is
not handled until Hyper starts actually polling the future.

We need to give `handle_request` an `Arc<Shared>`. By using the `move` keyword, we ensure
that the closure has ownership of an `Arc<Shared>`. Unfortunately, the closure can't just
give away ownership of the `Arc<Shared>` it has, because the closure is called once for
every request on the connection, and it can only give away ownership once. To avoid this
issue, the request handler will make a clone of the `Arc<Shared>` it owns, and give away
the clone instead.
```rs
let make_service = make_service_fn(move |client: &AddrStream| {
//                                 ^ give ownership of shared to closure
    let ip = client.remote_addr();
    let shared = shared.clone();
//               ^ make a clone to give to async block
    async move {
//        ^ shared is first moved into async block
        Ok::<_, Infallible>(service_fn(move |req| {
//                                     ^ then moved into closure
            let shared = shared.clone();
            handle_request(ip, req, shared)
        }))
    }
});
```
If we take a step back and look at the connection handler, we have the same situation. An
`Arc<Shared>` is moved into the closure, but the closure can be called multiple times, so
it must make a clone to give ownership away.

However an interesting thing happens in the async block: It does _not_ need to make a
clone. This is because the async block can only run once, so there is no problem with
giving away ownership of its `Arc<Shared>`. Note that the async block does not give
ownership away until it is polled. This is the same situation as the async function,
which does nothing until it is polled.

You might be wondering why the `ip` doesn't need to be cloned. This is because the
[`SocketAddr`] type is `Copy`, and if a type is `Copy`, you _can_ give ownership away
several times.

[`SocketAddr`]: https://doc.rust-lang.org/stable/std/net/enum.SocketAddr.html

## What if I want to block?
