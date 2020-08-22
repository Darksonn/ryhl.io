+++
title = "Temporarily opt-in to shared mutation"
date = 2020-08-15
description = "The purpose of this blog post is to celebrate the anniversary of two really neat methods on the Cell type: from_mut and as_slice_of_cells. Both methods were released in version 1.37.0 of Rust, exactly one year ago from the date this post was published."

[extra]
revised = 2020-08-15
keywords = "rust, shared, mutable, cell"
+++

The purpose of this blog post is to celebrate the anniversary of two really neat methods
on the [`Cell`] type:

 - [`Cell::from_mut`] This method turns a `&mut T` into a `&Cell<T>`.
 - [`Cell::as_slice_of_cells`] This method turns a `&Cell<[T]>` into a `&[Cell<T>]`.

Both methods were released in [version 1.37.0][rust-1.37] of Rust, exactly one year ago
from the date this post was published.

<!-- more -->

To explain why these methods are useful, we will be reimplementing [`Vec::retain`].
This is a method that lets you go through a vector and remove all items that fail to
match some condition. The items that are left in the vector are all shifted towards the
beginning of the array to remove holes, and remain in the same order as they were in
originally.

For the sake of simplicity, we will hard-code the condition to be "keep even integers".
Here is one way you might attempt to do it:
```rs
fn retain_even(nums: &mut Vec<i32>) {
    let mut i = 0;
    for num in nums.iter().filter(|&num| is_even(*num)) {
        nums[i] = *num;
        i += 1;
    }
    nums.truncate(i);
}
```
The basic idea is the following:

 1. The iterator goes through every integer, returning the even ones.
 2. We write the even integers back into the array.
 3. Since the iterator moves faster through the array than the index does, the iterator
    will never see or return a value after it has been modified.

Unfortunately the compiler does not like mixing up iteration and mutation like this.
```
error[E0502]: cannot borrow `*nums` as mutable because it is also borrowed as immutable
 --> src/lib.rs:4:9
  |
3 |     for num in nums.iter().filter(|&num| is_even(*num)) {
  |                ----------------------------------------
  |                |
  |                immutable borrow occurs here
  |                immutable borrow later used here
4 |         nums[i] = *num;
  |         ^^^^ mutable borrow occurs here

error: aborting due to previous error
```
This is a bit unfortunate, because it is a valid way of implementing the `retain`
algorithm. One way around this is to replace the iterator with indexes:
```rs
fn retain_even(nums: &mut Vec<i32>) {
    let mut i = 0;
    for j in 0..nums.len() {
        if is_even(nums[j]) {
            nums[i] = nums[j];
            i += 1;
        }
    }
    nums.truncate(i);
}
```
And this works! You can try it out [here][index-playground]. Unfortunately, this is a bit
restrictive. What if you don't want to add another index to your code?

It turns out that there is another way:
```rs
use std::cell::Cell;

fn retain_even(nums: &mut Vec<i32>) {
    let slice: &[Cell<i32>] = Cell::from_mut(&mut nums[..])
        .as_slice_of_cells();

    let mut i = 0;
    for num in slice.iter().filter(|num| is_even(num.get())) {
        slice[i].set(num.get());
        i += 1;
    }

    nums.truncate(i);
}
```
Perhaps surprisingly, this compiles. Try it out [here][cell-playground]. To understand
why, let us take a look at the two methods on [`Cell`] again.

 - [`Cell::from_mut`] This method turns a `&mut T` into a `&Cell<T>`.
 - [`Cell::as_slice_of_cells`] This method turns a `&Cell<[T]>` into a `&[Cell<T>]`.

In the snippet above, we first create an `&mut [i32]` using `&mut nums[..]`. We then turn
that into a `&Cell<[i32]>` using `from_mut` with `T = [i32]`, and that is then converted
into a `&[Cell<i32>]` using `as_slice_of_cells` with `T = i32`.  We can now proceed to
access the vector through `slice`. There are no compiler errors regarding mutating the
slice while it is borrowed, because calling [`set`][`Cell::set`] on a `Cell` takes _an
immutable reference to the `Cell`_.

## Why does this work?

The ability to modify something through an immutable reference is known as _interior
mutability_. You might have heard of it in the context of [`RefCell`], which is a type
that is very uncomfortable to use: It has a runtime cost and will panic if you use it
incorrectly. However, the `Cell` type has none of these problems: It is completely
zero-cost and can never panic whatsoever. One way to see that `Cell` must necessarily be
zero-cost is to notice that it stores no extra data — in fact, the `Cell::from_mut`
function is able to turn a reference to some memory without a `Cell` around it into a
reference with a `Cell` around it. This immediately gives away that a `Cell<T>` _must_
have the exact same memory representation as an `T`. In contrast, the `RefCell` type
includes a counter so it can verify the borrow rules at runtime, and as such there is no
`RefCell::from_mut` function.

To understand why it is safe to use `Cell` in this way, we have to talk about the various
properties of each reference. Let's start out with a summary:

| Property                    | `&T` | `&mut T` | `&Cell<T>` |
|-----------------------------|------|----------|------------|
| You can read                | Yes  | Yes      | Yes        |
| You can write               | No   | Yes      | Yes        |
| Others can read             | Yes  | No       | Yes        |
| Others can write            | No   | No       | Yes        |
| How many active references? | Many | One      | Many       |
| References in other threads | Yes  | No       | No         |
| Allows projection           | Yes  | Yes      | Sometimes  |

(We will discuss what projection is below.)

In the case of an **immutable reference**, it is guaranteed that the value behind the
reference is not modified while the reference exists. Note that an immutable reference
also prevents modification from other places while you hold the reference. This makes
it easy to verify safety: If the value is completely immutable, there is no possibility
of data races whatsoever.

In the case of an **mutable reference**, it is guaranteed that you have exclusive access
to the value, which makes it easy to verify safety: Nobody else is looking at the value,
so no data races will happen when you modify it. Additionally, it is fine to destroy
parts of the value (e.g. call `clear` on a vector), because nobody holds any references
to anywhere in the value, so no use-after-free can occur. It might have been better to
call it an [unique reference][unique].

And finally, in the case of **`&Cell<T>`**, it is guaranteed that all active references
to the value remain in the same thread. Unfortunately, shared mutation [can cause issues
even in single-threaded programs][single-threaded], so a `&Cell<T>` is quite limited in
what operations it is able to perform. For instance, you can't create any kind of
reference to the value stored inside the `Cell`. If you could make a `&mut T` to the
value, that would violate the uniqueness rule, as there may be other `&Cell<T>`s to the
same value. Similarly, making a `&T` to the value would violate the "others cannot write"
rule of immutable references.

This means that the only things you can do with a `&Cell<T>` are the following:

 * Set the current value.
 * When the value is `Copy`, make a copy of the current value.
 * Swap the current value with some other value.

Notice that getting the value requires it to be `Copy`. It isn't enough for it to be
`Clone`, because calling [`clone`] on the value would involve creating an immutable
reference to the value inside the `Cell`, and the implementation of `clone` might write
to the data through some other reference to the same `Cell` while that immutable
reference exists. This makes a `Cell<T>` quite difficult to use with types that are not
`Copy`, but it is not impossible. One way is to swap the value whenever you need to read
the current value. Another way is to wait until after all the `&Cell<T>` references have
gone away, at which point you can access the value normally. This is what happened when
we called `truncate` in our `retain_even` function.

### Downgrading a reference

When you have a mutable reference, you can downgrade it to other kinds of references. For
example, if `my_mut_ref` is a mutable reference to some value, you can do `&*my_mut_ref`
to obtain an immutable reference to the same value. This leaves the mutable reference
unusable until the immutable reference is no longer used.

Similarly, the [`Cell::from_mut`] function that we talk about in this post allows you to
downgrade a mutable reference into a `&Cell<T>`. It will basically allow you to
temporarily split your exclusive access into many pieces of shared access. Once you are
done with the shared access, you can regain the exclusive access, e.g. in our example we
can call `truncate` once we are done using the cells.

You cannot go between immutable and `&Cell<T>` references in either direction.

### Projection

Projection is when you take a reference to a large thing and create a reference to part
of that thing. There are several types of projection:

 - **Struct projection.** This is when you take a reference to a struct and create a
   reference to one of its fields.
 - **Enum projection.** This is when you take a reference to an enum and obtain a
   reference to fields in one of its variants. This is done by matching on the enum.
 - **Slice projection.** This is when you take a slice and obtain a smaller slice or a
   reference to an element in the slice.
 - **Collection-specific projection.** You can define your own types of projections for
   your own types. For instance, the vector type allows you to project a `&mut Vec<T>`
   into a `&mut [T]`, which gives up the ability to resize the vector.

Some kinds of projections are more cumbersome to do than others. For example, it is
possible to project a mutable reference into multiple disjoint mutable sub-references, as
each projection then has exclusive access to its own part of the larger value. However,
when dealing with slices, that can only be done with methods such as [`split_at_mut`] or
[`iter_mut`].

There are also some projections that it would not be safe to make. For instance, it
would be wrong to project a `&Cell<Vec<T>>` into an `&Cell<[T]>`, because if someone else
destroys the vector in the cell (by replacing it with another vector), that would
deallocate the memory that your `&Cell<[T]>` points into. That's a use-after-free. The
same problem exists with enums, since if someone replaces the enum with some other
variant, you might suddenly be holding a reference to a field that doesn't exist.  These
issues are also discussed [here][single-threaded].

So what kinds of projections _can_ you do with cells? You can do slice projection! In
fact, the [`Cell::as_slice_of_cells`] method lets you do exactly that. It would also be
safe to perform struct projection, as replacing the entire struct still leaves each field
in a valid state.  Unfortunately, the standard library provides no safe way to do so,
but [there are some crates that can do it][cell-project].

## Conclusion

My hope with this blog post is to bring some love to the `Cell` type. Whenever people
talk about interior mutability, the [`RefCell`] type gets all the ~~love~~ hate, but
`RefCell` isn't all there is to interior mutability: We have `Cell` too, and you don't
have to expose your programs to all the accidental panics you get with `RefCell`.

I hope that this blog post has given you a better idea of how references work in Rust,
and some tools to better handle various situations. If you ever feel the need to use
[`split_at_mut`], but find it awkward to do so, consider if `Cell` is a better fit.

You may also like [Rust: A unique perspective][unique], which covers the same topic from
a different angle.

[`Cell`]: https://doc.rust-lang.org/stable/std/cell/struct.Cell.html
[`Cell::from_mut`]: https://doc.rust-lang.org/stable/std/cell/struct.Cell.html#method.from_mut
[`Cell::as_slice_of_cells`]: https://doc.rust-lang.org/stable/std/cell/struct.Cell.html#method.as_slice_of_cells
[`Cell::set`]: https://doc.rust-lang.org/stable/std/cell/struct.Cell.html#method.set
[`RefCell`]: https://doc.rust-lang.org/stable/std/cell/struct.RefCell.html
[`Vec::retain`]: https://doc.rust-lang.org/stable/std/vec/struct.Vec.html#method.retain
[`clone`]: https://doc.rust-lang.org/stable/std/clone/trait.Clone.html#tymethod.clone
[`split_at_mut`]: https://doc.rust-lang.org/stable/std/primitive.slice.html#method.split_at_mut
[`iter_mut`]: https://doc.rust-lang.org/stable/std/primitive.slice.html#method.iter_mut
[index-playground]: https://play.rust-lang.org/?version=stable&mode=debug&edition=2018&gist=f341b19546f8e16cfb8db42be093c94c
[cell-playground]: https://play.rust-lang.org/?version=stable&mode=debug&edition=2018&gist=69f48de432a3ffc2fab9258a3397de64
[rust-1.37]: https://blog.rust-lang.org/2019/08/15/Rust-1.37.0.html
[single-threaded]: https://manishearth.github.io/blog/2015/05/17/the-problem-with-shared-mutability/
[unique]: https://limpet.net/mbrubeck/2019/02/07/rust-a-unique-perspective.html
[cell-project]: https://www.abubalay.com/blog/2020/01/05/cell-field-projection
