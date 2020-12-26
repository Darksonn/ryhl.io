+++
title = "Actors with Tokio"
date = 2020-10-01
description = ""
draft = true

[extra]
revised = 2020-09-30
keywords = "rust, tokio, actor, async, await, actix"
+++

This article is about building actors with Tokio directly, without using any
actor libraries such as Actix. This turns out to be rather easy to do, however
there are some details you should be aware of:

 1. Where to put the `tokio::spawn` call.
 2. Struct with `run` method vs bare function.
 3. Handles to the actor.
 4. Backpressure and bounded channels.
 5. Graceful shutdown.
 6. `Arc`, `Mutex` and shared state.

The techniques outlined in this article should work with any executor, but for
simplicity we will only talk about Tokio.  There is some overlap with the
[spawning] and [channel chapters] from the Tokio tutorial, and I recommend also
reading those chapters.

[spawning]: https://tokio.rs/tokio/tutorial/spawning
[channel chapters]: https://tokio.rs/tokio/tutorial/channels

<!-- more -->

Before we can talk about how to write an actor, we need to know what an actor
is. The basic idea behind an actor is to spawn a self-contained task that
performs some job independently of other parts of the program. Typically these
actors communicate with the rest of the program through the use of message
passing channels. Since actors run independently, programs designed using them
are naturally parallel.

A common use-case of actors is to assign the actor exclusive ownership of some
resource, and then let other tasks access this resource indirectly by talking to
the actor. For example, if you are implementing a chat server, you may spawn a
task for each connection, and a master task that routes chat messages between
the other tasks. This is useful because the master task can avoid having to deal
with network IO, and the connection tasks can focus exclusively on dealing with
network IO.

## The Recipe

An actor is split into two parts: the task and the handle. The task is the
separately spawned Tokio task that actually performs the duties of the actor,
and the handle is a struct that allows you to communicate with the task.

Let's consider a simple actor. The actor internally stores a counter that is
used to obtain some sort of unique id. The basic structure of the actor would be
something like the following:
```rust
use tokio::sync::{oneshot, mpsc};

struct MyActor {
    receiver: mpsc::Receiver<ActorMessage>,
    next_id: u32,
}
enum ActorMessage {
    GetUniqueId {
        respond_to: oneshot::Sender<u32>,
    },
}

impl MyActor {
    fn new(receiver: mpsc::Receiver<ActorMessage>) -> Self {
        MyActor {
            receiver,
            next_id: 0,
        }
    }
    fn handle_message(&mut self, msg: ActorMessage) {
        match msg {
            ActorMessage::GetUniqueId { respond_to } => {
                self.next_id += 1;

                // The `let _ =` ignores any errors when sending.
                //
                // This can happen if the `select!` macro is used
                // to cancel waiting for the response.
                let _ = respond_to.send(self.next_id);
            },
        }
    }
}

async fn run_my_actor(mut actor: MyActor) {
    while let Some(msg) = actor.receiver.recv().await {
        actor.handle_message(msg);
    }
}
```
Now that we have the actor itself, we also need a handle to the actor. A handle
is an object that other pieces of code can use to talk to the actor, and is also
what keeps the actor alive.

The handle will look like this:
```rust
#[derive(Clone)]
pub struct MyActorHandle {
    sender: mpsc::Sender<ActorMessage>,
}

impl MyActorHandle {
    pub fn new() -> Self {
        let (sender, receiver) = mpsc::channel(8);
        let actor = MyActor::new(receiver);
        tokio::spawn(run_my_actor(actor));

        Self { sender }
    }

    pub async fn get_unique_id(&self) -> u32 {
        let (send, recv) = oneshot::channel();
        let msg = ActorMessage::GetUniqueId {
            respond_to: send,
        };

        // Ignore send errors. If this send fails, so does the
        // recv.await below. There's no reason to check for the
        // same failure twice.
        let _ = self.sender.send(msg).await;
        recv.await.expect("Actor task has been killed")
    }
}
```
[full example](https://play.rust-lang.org/?version=stable&mode=debug&edition=2018&gist=1e60fb476843fb130db9034e8ead210c)

Let's take a closer look at the different pieces in this example.

**`ActorMessage.`** The `ActorMessage` enum defines the kind of messages we can
send to the actor. By using an enum, we can have many different message types,
and each message type can have its own set of arguments. We return a value to
the sender by using an [`oneshot`] channel, which is a message passing channel
that allows sending exactly one message.

In the example above, we match on the enum inside a `handle_message` method on
the actor struct, but that isn't the only way to structure this. One could also
match on the enum in the `run_my_actor` function. Each branch in this match
could then call various methods such as `get_unique_id` on the actor object.

**Errors when sending messages** When dealing with channels, not all errors are
fatal.  Because of this, the example sometimes uses `let _ =` to ignore errors.
Generally a `send` operation on a channel fails if the receiver has been
dropped.

The first instance of this in our example is the line in the actor where we
respond to the message we were sent. This can happen if the receiver is no
longer interested in the result of the operation, e.g. the task might that send
the message might have been killed.

**Shutdown of actor.** We can detect when the actor should shut down by looking
at failures to receive messages. In our example, this happens in the following
while loop:
```rust
while let Some(msg) = actor.receiver.recv().await {
    actor.handle_message(msg);
}
```
When all senders to the `receiver` have been dropped, we know that we will never
receive another message and can therefore shut down the actor. When this
happens, the call to `.recv()` returns `None`, and since it does not match the
pattern `Some(msg)`, the while loop exits and the function returns.

**`#[derive(Clone)]`** The `MyActorHandle` struct derives the `Clone` trait. It
can do this because [`mpsc`] means that it is a multiple-producer,
single-consumer channel. Since the channel allows multiple producers, we can
freely clone our handle to the actor, allowing us to talk to it from multiple
places.

[`oneshot`]: https://docs.rs/tokio/0.3/tokio/sync/oneshot/index.html
[`mpsc`]: https://docs.rs/tokio/0.3/tokio/sync/mpsc/index.html

## A run method on a struct

The example I gave above uses a top-level function that isn't defined on any
struct as the thing we spawn as a Tokio task, however many people find it more
natural to define a `run` method directly on the `MyActor` struct and spawn
that. This certainly works too, but the reason I give an example that uses a
top-level function is that it more naturally leads you towards the approach that
doesn't give you lots of lifetime issues.

To understand why, I have prepared an example of what people unfamiliar with the
pattern often come up with.
```rust
impl MyActor {
    fn run(&mut self) {
        tokio::spawn(async move {
            while let Some(msg) = self.receiver.recv().await {
                self.handle_message(msg);
            }
        });
    }

    pub async fn get_unique_id(&self) -> u32 {
        let (send, recv) = oneshot::channel();
        let msg = ActorMessage::GetUniqueId {
            respond_to: send,
        };

        // Ignore send errors. If this send fails, so does the
        // recv.await below. There's no reason to check for the
        // same failure twice.
        let _ = self.sender.send(msg).await;
        recv.await.expect("Actor task has been killed")
    }
}

... and no separate MyActorHandle
```
The two sources of trouble in this example are:

 1. The `tokio::spawn` call is inside `run`.
 2. The actor and the handle are the same struct.

The first issue causes problems because the `tokio::spawn` function requires the
argument to be `'static`. This means that the new task must own everything
inside it, which is a problem because the method borrows `self`, meaning that it
is not able to give away ownership of `self` to the new task.

The second issue causes problems because Rust enforces the single-ownership
principle. If you combine both the actor and the handle into a single struct,
you are (at least from the compiler's perspective) giving every handle access to
the fields owned by the actor's task. E.g. the `next_id` integer should be owned
only by the actor's task, and should not be directly accessible from any of the
handles.

That said, there is a version that works. By fixing the two above problems, you
end up with the following:
```rust
impl MyActor {
    async fn run(mut self) {
        while let Some(msg) = self.receiver.recv().await {
            self.handle_message(msg);
        }
    }
}

impl MyActorHandle {
    pub fn new() -> Self {
        let (sender, receiver) = mpsc::channel(8);
        let actor = MyActor::new(receiver);
        tokio::spawn(actor.run());

        Self { sender }
    }
}
```
This works identically to the top-level function.

## Using actors for connections

Let's say we want to implement a chat server. We might define the following
Tokio tasks:

 1. A listener for new connections.
 2. A message router that moves messages between connections.
 3. A task for each connection that takes care of the actual network IO.


