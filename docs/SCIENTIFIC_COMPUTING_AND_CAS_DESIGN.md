# Scientific Computing, Top-Tier Physical Units, and Built-in CAS in Tōast

> **Status:** Architectural Proposal & Design Specification  
> **Target:** Tōast Language & TōSh Shell Suite  
> **Related Documents:** [`docs/UNIT_SYSTEM_STABILIZATION.md`](file:///home/komrad/projects/tosh/docs/UNIT_SYSTEM_STABILIZATION.md), [`docs/TOAST_SEPARATION_PLAN.md`](file:///home/komrad/projects/tosh/docs/TOAST_SEPARATION_PLAN.md), [`docs/ARCHITECTURE.md`](file:///home/komrad/projects/tosh/docs/ARCHITECTURE.md)

---

## 1. Executive Summary & Vision

Tōast occupies a unique space: it combines an interactive shell environment with a strongly-typed, modern general-purpose programming language. However, engineering, research, and scientific computing require capabilities that conventional shell and general-purpose languages treat as second-class libraries:
1. **Mathematical expression and array semantics** (as in Julia, APL, and Fortran).
2. **First-class dimensional analysis and physical units** (as in Frink, F#, and Numbat).
3. **Symbolic computation and computer algebra** (as in Wolfram Language / Mathematica).

This document outlines the architectural roadmap to elevate Tōast into a **tier-one language for scientific and engineering computing**. It surveys the modern scientific programming landscape, specifies a next-generation physical unit system that surpasses existing language implementations, and details the design of a native, built-in Computer Algebra System (CAS).

---

## 2. Research & Scientific Computing Languages Survey

```
       ┌─────────────────────────────────────────────────────────────┐
       │                 Scientific Computing Landscape              │
       └─────────────────────────────────────────────────────────────┘
          │                   │                     │              │
    [Array / Vector]    [Symbolic / CAS]      [Dimensional]   [Modern Systems]
     APL / BQN / J       Wolfram Language        Frink            Julia
     MATLAB / Octave     Macsyma / Reduce        F# (UoM)         Fortress
     Fortran (2018+)     SymPy                   Numbat           Rust (uom)
```

### Comparative Analysis of Core Paradigms

| Language | Core Strengths | Critical Limitations | Actionable Takeaway for Tōast |
| :--- | :--- | :--- | :--- |
| **Julia** | Multiple dispatch; broadcast fusion (`.+`, `.*`, `f.(x)`); expression metaprogramming (`Expr`); dual numbers for Automatic Differentiation (AD). | High time-to-first-plot (TTFP) compilation latency; heavy runtime; complex type-instantiation explosion. | **Broadcast operators** for element-wise vector/matrix operations; **symmetric operator dispatch** across numeric/unit types. |
| **Wolfram Language** | Everything is an expression (`Expr[Head, Args]`); term-rewriting pattern engine (`/.`); unified exact calculus; built-in physical constants. | Closed proprietary runtime; non-standard syntax unsuitable for systems/shell workflows; unpredictable heuristic simplification. | **Homoiconic symbolic math domain**; pattern rewriting rules (`expr /. rule`); exact rational math default for symbolic terms. |
| **Frink** | Gold standard in dynamic physical unit calculations; automatic cancellation; currency/planetary databases; interval arithmetic ($10 \pm 0.2$). | Purely interpreted; no compile-time type verification; zero static optimization; lacks tensor/matrix primitives. | **Full SI prefix synthesis**; **interval/uncertainty propagation**; **auto-scaling display formatting** (`0.000045 s` $\to$ `45 µs`). |
| **F#** | Zero-overhead static Units of Measure (`float<m/s^2>`); dimensional consistency checked at compile-time; erased to raw primitives in IL. | Strictly compile-time; cannot handle dynamic units at REPL/shell pipes or file inputs without reflection or boxing. | **Dual-Tier Model**: Erased zero-cost unit structs in compiled assemblies + self-describing dynamic `Quantity` in shell pipelines. |
| **Fortress** | Guy Steele's mathematical language; juxtaposition multiplication ($2x$, $\sin x$); superscript exponents; native dimensional types. | Whitespace-sensitive grammar led to catastrophic parsing ambiguities; project discontinued. | **Disambiguated juxtaposition** in mathematical contexts; Unicode mathematical operators; clean operator precedence. |
| **APL / BQN / J** | Rank polymorphism (functions automatically adapt across scalars, vectors, and N-D arrays); leading-axis theory; tacit pipelines. | Cryptic ASCII/Unicode glyph soup; high cognitive overhead; friction with shell command semantics. | **Rank-aware pipeline operations** (generalizing `get`, `row`, `where` over matrices and tabular data). |
| **Fortran (2018+)** | Contiguous column-major memory layout; array slicing (`A(1:N:2)`); pure elemental functions; automatic SIMD vectorization. | Archaic syntax; poor string/data handling; absence of metaprogramming or reflection. | **Contiguous SIMD memory buffers** (`QuantityArray`) avoiding boxed object arrays in numerical pipelines. |
| **Numbat** | Modern statically typed physical unit DSL; algebraic dimension definitions; clear dimensional error diagnostics. | Domain-specific calculator only; lacks general-purpose language and shell constructs. | **Dimensional diagnostic clarity**; strict affine temperature separation. |

---

## 3. Designing a World-Class Physical Unit System for Tōast

Tōast currently implements initial unit capabilities in [`Quantity.cs`](file:///home/komrad/projects/tosh/src/Toast.Runtime/Units/Quantity.cs), [`UnitDefinition.cs`](file:///home/komrad/projects/tosh/src/Toast.Runtime/Units/UnitDefinition.cs), and [`UnitExpression.cs`](file:///home/komrad/projects/tosh/src/Toast.Runtime/Units/UnitExpression.cs), guided by [`docs/UNIT_SYSTEM_STABILIZATION.md`](file:///home/komrad/projects/tosh/docs/UNIT_SYSTEM_STABILIZATION.md).

To build a **top-tier unit system across all programming languages**, Tōast must resolve seven fundamental architectural challenges:

```
       ┌────────────────────────────────────────────────────────┐
       │             Tōast Unified Quantity Model               │
       └────────────────────────────────────────────────────────┘
                                   │
         ┌─────────────────────────┴─────────────────────────┐
         ▼                                                   ▼
   [Dynamic Shell Tier]                             [Compiled Static Tier]
   • Boxed `Quantity` object                        • Generic value type `Quantity<Dim, Kind>`
   • Runtime dimension vectors                      • Zero-allocation IL struct
   • Shell pipeline reflection                      • Erased to `double` or SIMD vector
   • Dynamic parsing & formatting                   • Compile-time dimensional verification
```

### Pillar 1: Dual-Tier Execution Model (F# + Frink Synthesis)
- **Interactive Shell / REPL Tier**: First-class `Quantity` object storing magnitude, unit symbol, dimensional vector, and conversion scaling. Enables runtime introspection, dynamic unit parsing from CLI input, and pipeline operations.
- **Compiled Tier (`tosh --compile`)**: Statically checked dimensional types. Functions declare physical signatures:
  ```tosh
  func kinetic_energy(m: Mass, v: Velocity) -> Energy {
      return 0.5 * $m * ($v ** 2)
  }
  ```
  The compiler's binder validates dimensions at compile time and erases the unit wrapper into bare 64-bit IEEE floats (`double`) or SIMD registers, achieving native C/Fortran execution performance.

### Pillar 2: Rational Exponents & Fractional Dimensions
Scientific computing frequently encounters fractional dimensions:
- **Noise Spectral Density**: $V / \sqrt{\text{Hz}} = \text{V} \cdot \text{s}^{1/2}$
- **Fracture Mechanics (Stress Intensity Factor)**: $\text{MPa} \cdot \sqrt{\text{m}}$
- **Specific Detectivity ($D^*$)**: $\text{cm} \cdot \sqrt{\text{Hz}} / \text{W}$

**Architecture**: Replace `int` exponents in [`UnitExpression.cs`](file:///home/komrad/projects/tosh/src/Toast.Runtime/Units/UnitExpression.cs) with an immutable `Rational(int Numerator, int Denominator)` struct:
```tosh
var noise = 15`nV/sqrt(Hz)
var bw = 10`kHz
var total_noise = $noise * sqrt($bw)  # Produces 1.5`uV (auto-cancels s^(1/2) and s^(-1/2))
```

### Pillar 3: Decoupling Physical Dimension from Semantic Kind
Identical base dimensions can represent incompatible physical phenomena:
- **Energy vs. Torque**: Both decompose to $M \cdot L^2 \cdot T^{-2}$. Adding $10\text{ J}$ of thermal energy to $5\text{ N}\cdot\text{m}$ of torque is a physical category error.
- **Radiation**: Absorbed Dose (Gray = $\text{J/kg}$) vs. Dose Equivalent (Sievert = $\text{J/kg}$).
- **Frequency vs. Angular Rate vs. Decay**: $\text{Hz}$ (cycles/s), $\text{rad/s}$ (angle/s), and $\text{Bq}$ (disintegrations/s) all share $T^{-1}$.

**Architecture**: Unit definitions carry both a `DimensionVector` and an optional `SemanticKind`:
```csharp
public sealed record SemanticKind(string Name, UnitExpression BaseDimension);
// SemanticKind.Energy  vs SemanticKind.Torque
// SemanticKind.AbsorbedDose vs SemanticKind.DoseEquivalent
```
Derived arithmetic detects cross-kind collisions and rejects invalid addition/subtraction while permitting valid multiplication/division.

### Pillar 4: Affine Spaces & Point/Delta Arithmetic
Affine quantities (temperatures, gauge pressures, timestamps) have an origin and cannot be treated as linear vectors:

```
       Point - Point       ───►  Delta
       Point ± Delta       ───►  Point
       Delta ± Delta       ───►  Delta
       Point + Point       ───►  [COMPILE/RUNTIME ERROR]
       Point * Scalar      ───►  [COMPILE/RUNTIME ERROR]
       Delta * Scalar      ───►  Delta
```

**Syntax & Semantics**:
```tosh
var t1 = 20°C             # Point (Absolute Temperature)
var dt = 5`deltaC         # Delta (Temperature Difference)
var t2 = $t1 + $dt        # 25°C (Point)
var diff = $t2 - $t1      # 5 deltaC (Delta)
# $t1 + $t2              # ERROR: Cannot add two absolute temperature points
# $t1 * 2                 # ERROR: Cannot multiply an absolute temperature point
```

### Pillar 5: Logarithmic and Decibel Scales
A complete engineering unit system must support nonlinear/logarithmic units:
- Power ratios: $\text{dB} = 10 \log_{10}(P / P_0)$ ($\text{dBm}$ referenced to $1\text{ mW}$, $\text{dBW}$ to $1\text{ W}$)
- Field ratios: $\text{dB} = 20 \log_{10}(V / V_0)$ ($\text{dBV}$ referenced to $1\text{ V}$, $\text{dBu}$ to $0.775\text{ V}$)
- Acoustics: $\text{dBSPL}$ referenced to $20\ \mu\text{Pa}$
- Chemistry: $\text{pH} = -\log_{10}[H^+]$

Logarithmic units encapsulate their reference level and scale factor, supporting automatic logarithmic addition (power summing) and linear conversion.

### Pillar 6: Measurement Uncertainty & Tolerance Propagation
Engineering calculations require tracking error bounds:
```tosh
var length = 12.4 +/- 0.2 `m
var width  = 4.1 +/- 0.1 `m
var area   = $length * $width
echo $area   # 50.84 +/- 1.49 m^2 (Gaussian quadrature error propagation)
```
Tōast quantities support an optional variance/uncertainty component that propagates through both linear and non-linear mathematical operations ($f(x \pm \sigma_x) \approx f(x) \pm |f'(x)|\sigma_x$).

### Pillar 7: High-Performance Vectorized Storage (`QuantityArray`)
In array processing, allocating individual `Quantity` objects induces severe GC pressure and cache misses.
- **`QuantityArray`**: Stores a contiguous unmanaged memory buffer (`Memory<double>` or SIMD `Vector<double>`) paired with a single shared `UnitDefinition`.
- Arithmetic operations (`$arrayA + $arrayB`, `$array * 2`) dispatch to SIMD vector instructions and emit a single new `QuantityArray` without per-element boxing.

---

## 4. Architecting a Built-in Computer Algebra System (CAS)

Rather than delegating symbolic math to an external process or optional library, Tōast can integrate a native CAS engine directly into its language architecture, inspired by the Wolfram Language.

```
                  ┌─────────────────────────────────────────┐
                  │       Tōast Execution Pipeline          │
                  └─────────────────────────────────────────┘
                                       │
                    ┌──────────────────┴──────────────────┐
                    ▼                                     ▼
          [Numeric / Shell Mode]                 [Symbolic CAS Mode]
          • IEEE 754 Floats / Decimals           • Exact Rationals (BigRational)
          • Eager variable evaluation            • Symbolic expression trees (SymExpr)
          • Direct machine arithmetic            • Term rewriting & pattern rules (/. )
          • Shell pipeline streaming             • Calculus (diff, integrate, solve)
```

### 1. Language Ergonomics & Shell Disambiguation
To prevent conflicts between bareword shell commands and symbolic mathematical variables, Tōast provides two complementary ergonomic avenues:

1. **Declared Symbolic Variables (`sym`)**:
   ```tosh
   sym x, y, theta
   var f = ($x + 1) ** 2
   echo $f                 # Evaluates to symbolic term: (x + 1)^2
   echo (expand $f)        # x^2 + 2*x + 1
   ```
2. **Mathematical Quotation Blocks (`@(...)` or `math { ... }`)**:
   Inside a mathematical block, bare identifiers default to algebraic symbols:
   ```tosh
   var f = @( sin(x)**2 + cos(x)**2 )
   echo (simplify $f)      # Evaluates to 1
   ```

### 2. Internal Runtime Architecture

```
  ┌────────────────────────────────────────────────────────────────────────┐
  │                           CAS Core Subsystems                          │
  ├────────────────────────────────────────────────────────────────────────┤
  │ 1. Immutable SymExpr DAG (Hash-consed, canonicalized expression tree)   │
  │ 2. Exact Arithmetic Engine (BigInteger, BigRational, Complex, Radicals)│
  │ 3. Pattern Matching & Term Rewriting Engine (x_ + x_ => 2*x)          │
  │ 4. Symbolic Calculus & Transformation Library                          │
  └────────────────────────────────────────────────────────────────────────┘
```

#### A. Immutable Expression Representation (`SymExpr`)
All symbolic expressions compile into an immutable, hash-consed Directed Acyclic Graph (DAG):
```csharp
public abstract record SymExpr : IComparable<SymExpr>
{
    public abstract int CompareTo(SymExpr? other);
}

public sealed record SymNumber(BigRational Value) : SymExpr;
public sealed record SymSymbol(string Name) : SymExpr;
public sealed record SymQuantity(BigRational Magnitude, UnitExpression Dimension, string Symbol) : SymExpr;
public sealed record SymApply(SymbolicOp Op, ImmutableArray<SymExpr> Args) : SymExpr;
```
- **Total Canonical Ordering**: Commutative operators (`+`, `*`) automatically sort their arguments upon construction. Consequently, expressions like `y + x` and `x + y` map to identical canonical nodes, enabling instant identity checks ($O(1)$ equality via hash consing).

#### B. Exact Arithmetic Subsystem
Symbolic expressions operate on exact mathematical types:
- `BigInteger`: Arbitrary-precision integer arithmetic.
- `BigRational`: Exact numerator/denominator pairs (e.g. $1/3 + 1/6 \to 1/2$).
- `SymRadical`: Exact algebraic roots (e.g. $\sqrt{8} \to 2\sqrt{2}$).
- `Complex<BigRational>`: Exact Gaussian rationals.

#### C. Pattern Matching & Term-Rewriting Engine
Mirroring the Wolfram Language's transformation model:
- Patterns with blanks: `$x_` matches any expression; `$x_Integer` matches integers; `$x_?Positive` matches positive values.
- Transformation operator `/.` (Replace All) and `//.` (Replace Repeated until fixed point):
```tosh
sym x, a, b
var expr = sin(a + b)
var addition_formula = sin($u_ + $v_) => sin($u_)*cos($v_) + cos($u_)*sin($v_)

echo ($expr /. $addition_formula)
# Produces: sin(a)*cos(b) + cos(a)*sin(b)
```

#### D. Core Mathematical Operations
1. **Symbolic Differentiation (`diff`)**:
   Full support for elementary functions, chain rule, product rule, quotient rule, and implicit differentiation:
   ```tosh
   sym x
   diff (3*$x**3 - 5*$x + 7) $x   # 9*x^2 - 5
   ```
2. **Equation Solving (`solve`)**:
   Exact solutions for linear systems, quadratics, cubics, quartics, and polynomial systems via Groebner bases; transcendental equations via Lambert $W$ and root isolation:
   ```tosh
   solve ($x**2 - 5*$x + 6 == 0) for $x   # [2, 3]
   ```
3. **Series Expansion (`series`)**:
   Taylor, Maclaurin, and Laurent expansions around a specified point to order $O(x^n)$:
   ```tosh
   series (exp $x) $x around 0 order 4   # 1 + x + x^2/2 + x^3/6 + O(x^4)
   ```
4. **Symbolic Integration (`integrate`)**:
   Table-driven antiderivatives, integration by parts, partial fraction decomposition, and heuristic Risch algorithm for elementary transcendental functions.

---

## 5. The Grand Convergence: Units + CAS + Plotting

The unified strength of Tōast emerges when these systems interact natively:

```
      ┌──────────────────────────────────────────────────────────────┐
      │               The Scientific Computing Triangle              │
      └──────────────────────────────────────────────────────────────┘
                                     ▲
                                    / \
                                   /   \
                                  /     \
                                 /  CAS  \
                                /         \
                               /           \
               [Physical Units] ─────────── [ToastLib.Plot]
```

1. **Dimensional Calculus**:
   Differentiating a physical trajectory automatically transforms and preserves physical dimensions:
   ```tosh
   sym t
   var position = 4.9`m/s^2 * ($t ** 2)
   var velocity = diff $position $t
   echo $velocity
   # (9.8 m/s^2) * t   (Dimension: Length / Time)
   ```
2. **Automated Physical Formula Verification**:
   The CAS can symbolically verify dimensional homogeneity before numerical simulation:
   ```tosh
   assert (dimension_of ($force * $distance) == dimension_of ($mass * $velocity ** 2))
   ```
3. **Symbolic Plotting via `ToastLib.Plot`**:
   Symbolic expressions can be directly passed to plotting commands without manual numerical discretization:
   ```tosh
   sym x
   var f = sin($x) / $x
   plot $f for $x in -10..10 title="Sinc Function"
   ```

---

## 6. Staged Implementation Roadmap

```
  Phase 1: Unit System Hardening (Current TS-P3-07)
  ├── Rational exponents in UnitExpression
  ├── SemanticKind separation (Energy vs. Torque)
  └── Affine temperature points vs. deltas

  Phase 2: Vectorized Quantities & Compiler Erasure
  ├── QuantityArray contiguous SIMD memory buffers
  └── Compiler binder unit verification and IL value-type erasure

  Phase 3: Core CAS Foundation
  ├── BigRational and exact arithmetic engine
  ├── Hash-consed immutable SymExpr DAG
  └── Canonical sorting and automatic simplification rules

  Phase 4: Pattern Rewriting & Calculus
  ├── Wolfram-style pattern matching and `/.` rewriting
  ├── Symbolic differentiation, Taylor series, and polynomial algebra
  └── Analytical equation solver

  Phase 5: Full Ecosystem Integration
  ├── Dimensional calculus (Units in CAS)
  └── Direct symbolic plotting in ToastLib.Plot
```
