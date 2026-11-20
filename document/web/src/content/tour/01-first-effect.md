---
title: Your first effect
---

An effect describes work without running it. `Effect.Gen` takes an `async` block; nothing inside it runs until you call `RunAsync`.

Press **Run**. `Effect created.` is printed before `Preparing the order.`, because creating the effect only stores the block.

`RunAsync` returns a `Result`. An `Ok` holds the value the block returned. The error type here is `Never`: this effect cannot fail with an expected error.

Try calling `RunAsync` twice. The block runs once per call.
