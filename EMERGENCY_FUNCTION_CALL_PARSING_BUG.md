# TōSh Function Call Tuple Parsing Emergency

## Summary

**Critical Bug:**
Function calls with multiple arguments in parentheses are being parsed as a single tuple argument, not as separate arguments. This breaks all multi-argument function calls in TōSh.

---

## Minimal Repro Case

```tosh
func test_args(a, b, c) {
    echo $a
    echo $b
    echo $c
}

test_args(1, 2, 3)  # Should print 1, 2, 3 on separate lines
```

**Actual behavior:**
- The function receives a single argument (tuple or list: (1, 2, 3)), not three separate arguments.
- Results in argument count/type errors or incorrect values.

**Expected behavior:**
- The function receives three arguments: 1, 2, 3.
- Each is bound to its respective parameter.

---

## Impact
- Breaks all multi-argument function calls and all code relying on correct argument binding.
- Affects user scripts, standard library, and all downstream tooling.
- Regression risk: High, as this is a core language feature.

---

## Steps to Fix
1. **Parser:**
   - Ensure that comma-separated values in function call parentheses are parsed as separate arguments, not as a tuple/list/record, unless explicit tuple/record syntax is used.
2. **Test Suite:**
   - Add regression tests for multi-argument function calls, including edge cases (nested calls, default/optional/rest args).
3. **Review:**
   - Audit the parser and evaluator for any other places where tuple/list/record confusion may occur.

---

## Priority
**EMERGENCY: Language-wide breakage.**

---

## Example Regression Test

```tosh
func add(a, b) { return $a + $b }
echo add(2, 3)  # Should print 5
```

---

## Additional Notes
- This bug may also affect method calls, command invocations, and any macro or DSL features that use parentheses for argument grouping.
- Fix should be backported to all maintained branches.

---

/cc @komrad @tosh-maintainers
