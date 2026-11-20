---
title: Expected errors
---

Failures you plan for belong in the error type. Declare them as cases of a `union` marked `[Error]`; the source generator fills in the rest.

Inside `Effect.Gen`, `scope.Get` unwraps a `Result`. On `Err`, the block stops there and the effect fails with that error, so the lines after it only see valid values.

Press **Run** to see one order succeed and one fail. Then add an `OutOfStock` check and handle it at the call site.
