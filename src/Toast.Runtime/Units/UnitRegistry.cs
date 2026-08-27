namespace Tosh.Runtime.Units;

/// <summary>
/// Central registry of all known units. Resolves unit symbols (including SI prefix + base combinations),
/// maps dimensions to named category types, and supports user-defined units.
/// </summary>
public sealed class UnitRegistry
{
    private static readonly Lazy<UnitRegistry> LazyInstance = new(() => new UnitRegistry());
    public static UnitRegistry Instance => LazyInstance.Value;

    private readonly object _gate = new();
    private readonly Dictionary<string, UnitDefinition> _units = new(StringComparer.Ordinal);
    private readonly Dictionary<UnitExpression, string> _dimensionToCategory = new();
    private readonly Dictionary<UnitExpression, string> _dimensionToCanonicalUnit = new();
    private readonly Dictionary<UnitExpression, Func<double, string, Quantity>> _namedTypeFactories = new();
    private readonly Dictionary<string, Func<double, string, Quantity>> _categoryTypeFactories =
        new(StringComparer.OrdinalIgnoreCase);

    private UnitRegistry()
    {
        RegisterBuiltInUnits();
        RegisterNamedTypeFactories();
    }

    #region Public API

    /// <summary>Resolve a unit symbol (e.g. "km", "mph", "degC") to its definition.</summary>
    public UnitDefinition? TryResolve(string symbol)
    {
        lock (_gate)
        {
            if (_units.TryGetValue(symbol, out var unit))
            {
                return unit;
            }

            // Try SI prefix decomposition: e.g. "km" → prefix "k" + base "m"
            return TryResolvePrefixed(symbol);
        }
    }

    /// <summary>Get the named category for a dimension expression, or null if unknown.</summary>
    public string? GetCategoryForDimension(UnitExpression dimension)
    {
        return _dimensionToCategory.TryGetValue(dimension, out var category) ? category : null;
    }

    /// <summary>
    /// Returns a stable base-unit presentation for a dimension. Named SI units
    /// (J, W, N, and so on) win when one was registered; arbitrary dimensions
    /// fall back to a parseable expression of base units.
    /// </summary>
    public string GetCanonicalUnitSymbol(UnitExpression dimension)
    {
        return _dimensionToCanonicalUnit.TryGetValue(dimension, out var symbol)
            ? symbol
            : dimension.ToCanonicalUnitSymbol();
    }

    /// <summary>
    /// Create a properly typed Quantity (Length, Mass, etc.) based on its dimension.
    /// Falls back to raw Quantity if no named type matches.
    /// </summary>
    public Quantity CreateTyped(double magnitude, UnitExpression dimension, string unitSymbol)
    {
        lock (_gate)
        {
            return CreateTypedCore(magnitude, dimension, unitSymbol);
        }
    }

    /// <summary>
    /// Creates a displayed quantity from a value already expressed in the
    /// dimension's base unit. Compound and affine targets retain their complete
    /// transform.
    /// </summary>
    public Quantity CreateTypedFromBase(double baseValue, UnitExpression dimension, string unitSymbol)
    {
        lock (_gate)
        {
            if (!UnitExpressionParser.TryParseConversion(
                    unitSymbol,
                    out var conversion,
                    out var parsedDimension,
                    out var normalizedSymbol) ||
                parsedDimension != dimension)
            {
                throw new ArgumentException(
                    $"Unit '{unitSymbol}' does not describe dimension '{dimension}'.",
                    nameof(unitSymbol));
            }

            return CreateTypedCore(conversion.FromBase(baseValue), dimension, normalizedSymbol);
        }
    }

    /// <summary>Compatibility bridge for the former ambiguous boolean factory.</summary>
    [Obsolete("Use CreateTyped for display magnitudes or CreateTypedFromBase for base values.")]
    public Quantity CreateTyped(
        double magnitude,
        UnitExpression dimension,
        string unitSymbol,
        bool fromBase)
    {
        return fromBase
            ? CreateTypedFromBase(magnitude, dimension, unitSymbol)
            : CreateTyped(magnitude, dimension, unitSymbol);
    }

    private Quantity CreateTypedCore(double magnitude, UnitExpression dimension, string unitSymbol)
    {

        // A named unit carries more meaning than its dimensions alone. Nm and J
        // are dimensionally equal, for example, but the former is torque and the
        // latter energy. Preserve that category when the display symbol resolves
        // directly; compound/derived expressions use the dimension fallback.
        var definition = TryResolve(unitSymbol);
        if (definition is not null &&
            definition.Dimension == dimension &&
            _categoryTypeFactories.TryGetValue(definition.Category, out var categoryFactory))
        {
            return categoryFactory(magnitude, unitSymbol);
        }

        if (_namedTypeFactories.TryGetValue(dimension, out var factory))
        {
            return factory(magnitude, unitSymbol);
        }

        return new Quantity(magnitude, dimension, unitSymbol);
    }

    /// <summary>Register a user-defined unit at runtime.</summary>
    public void RegisterUnit(UnitDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!definition.IsUserDefined)
        {
            throw new ArgumentException(
                "Public unit registration requires IsUserDefined=true.",
                nameof(definition));
        }

        lock (_gate)
        {
            if (_units.ContainsKey(definition.Symbol))
            {
                throw new InvalidOperationException(
                    $"Unit symbol '{definition.Symbol}' is already registered and cannot be replaced.");
            }

            _units.Add(definition.Symbol, definition);
        }
    }

    /// <summary>Remove a user-defined unit.</summary>
    public bool RemoveUnit(string symbol)
    {
        lock (_gate)
        {
            if (_units.TryGetValue(symbol, out var unit) && unit.IsUserDefined)
            {
                _units.Remove(symbol);
                return true;
            }

            return false;
        }
    }

    /// <summary>Get all registered units, optionally filtered by category.</summary>
    public IEnumerable<UnitDefinition> GetAllUnits(string? category = null)
    {
        UnitDefinition[] units;
        lock (_gate)
        {
            units = _units.Values.ToArray();
        }

        if (category is not null)
        {
            units = units.Where(u => string.Equals(u.Category, category, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        return units.OrderBy(u => u.Category).ThenBy(u => u.Symbol);
    }

    /// <summary>Get all category names.</summary>
    public IEnumerable<string> GetCategories()
    {
        lock (_gate)
        {
            return _units.Values
                .Select(u => u.Category)
                .Concat(_categoryTypeFactories.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    #endregion

    #region SI Prefix Resolution

    private static readonly (string prefix, double factor)[] SiPrefixes =
    [
        ("Q", 1e30),
        ("R", 1e27),
        ("Y", 1e24),
        ("Z", 1e21),
        ("E", 1e18),
        ("P", 1e15),
        ("T", 1e12),
        ("G", 1e9),
        ("M", 1e6),
        ("k", 1e3),
        ("h", 1e2),
        ("da", 1e1),
        ("d", 1e-1),
        ("c", 1e-2),
        ("m", 1e-3),
        ("u", 1e-6),
        ("μ", 1e-6),
        ("n", 1e-9),
        ("p", 1e-12),
        ("f", 1e-15),
        ("a", 1e-18),
        ("z", 1e-21),
        ("y", 1e-24),
        ("r", 1e-27),
        ("q", 1e-30),
    ];

    private UnitDefinition? TryResolvePrefixed(string symbol)
    {
        foreach (var (prefix, prefixFactor) in SiPrefixes)
        {
            if (!symbol.StartsWith(prefix, StringComparison.Ordinal) || symbol.Length <= prefix.Length)
            {
                continue;
            }

            var baseSymbol = symbol[prefix.Length..];

            if (!_units.TryGetValue(baseSymbol, out var baseUnit))
            {
                continue;
            }

            if (!baseUnit.AllowSiPrefixes) continue;

            var prefixed = new UnitDefinition(
                symbol,
                $"{prefix}{baseUnit.Name}",
                baseUnit.Category,
                baseUnit.Dimension,
                baseUnit.ToBaseFactor * prefixFactor,
                baseUnit.ToBaseOffset,
                isUserDefined: false,
                allowSiPrefixes: false,
                role: baseUnit.Role);

            // Resolution is a read and must not mutate the process-global registry.
            return prefixed;
        }

        return null;
    }

    #endregion

    #region Built-in Registration

    private void RegisterBuiltInUnits()
    {
        // ── SI Base Units ──────────────────────────────────────────
        var length = UnitExpression.Of(UnitDimension.Length);
        var mass = UnitExpression.Of(UnitDimension.Mass);
        var time = UnitExpression.Of(UnitDimension.Time);
        var current = UnitExpression.Of(UnitDimension.ElectricCurrent);
        var temperature = UnitExpression.Of(UnitDimension.Temperature);
        var substance = UnitExpression.Of(UnitDimension.AmountOfSubstance);
        var luminosity = UnitExpression.Of(UnitDimension.LuminousIntensity);
        var data = UnitExpression.Of(UnitDimension.Data);
        var angle = UnitExpression.Of(UnitDimension.Angle);

        // Derived dimension shortcuts
        var area = UnitExpression.Of(UnitDimension.Length, 2);
        var volume = UnitExpression.Of(UnitDimension.Length, 3);
        var speed = UnitExpression.Of((UnitDimension.Length, 1), (UnitDimension.Time, -1));
        var acceleration = UnitExpression.Of((UnitDimension.Length, 1), (UnitDimension.Time, -2));
        var force = UnitExpression.Of((UnitDimension.Mass, 1), (UnitDimension.Length, 1), (UnitDimension.Time, -2));
        var energy = UnitExpression.Of((UnitDimension.Mass, 1), (UnitDimension.Length, 2), (UnitDimension.Time, -2));
        var power = UnitExpression.Of((UnitDimension.Mass, 1), (UnitDimension.Length, 2), (UnitDimension.Time, -3));
        var pressure = UnitExpression.Of((UnitDimension.Mass, 1), (UnitDimension.Length, -1), (UnitDimension.Time, -2));
        var frequency = UnitExpression.Of(UnitDimension.Time, -1);
        var charge = UnitExpression.Of((UnitDimension.ElectricCurrent, 1), (UnitDimension.Time, 1));
        var voltage = UnitExpression.Of((UnitDimension.Mass, 1), (UnitDimension.Length, 2), (UnitDimension.Time, -3), (UnitDimension.ElectricCurrent, -1));
        var resistance = UnitExpression.Of((UnitDimension.Mass, 1), (UnitDimension.Length, 2), (UnitDimension.Time, -3), (UnitDimension.ElectricCurrent, -2));
        var capacitance = UnitExpression.Of((UnitDimension.ElectricCurrent, 2), (UnitDimension.Time, 4), (UnitDimension.Mass, -1), (UnitDimension.Length, -2));
        var inductance = UnitExpression.Of((UnitDimension.Mass, 1), (UnitDimension.Length, 2), (UnitDimension.Time, -2), (UnitDimension.ElectricCurrent, -2));
        var density = UnitExpression.Of((UnitDimension.Mass, 1), (UnitDimension.Length, -3));
        var torque = energy; // Same dimension as energy — distinguished by named type
        var flowRate = UnitExpression.Of((UnitDimension.Length, 3), (UnitDimension.Time, -1));
        var angularVelocity = UnitExpression.Of((UnitDimension.Angle, 1), (UnitDimension.Time, -1));

        // ── Length ─────────────────────────────────────────────────
        Register("m", "meter", "Length", length, 1.0);
        Register("in", "inch", "Length", length, 0.0254);
        Register("ft", "foot", "Length", length, 0.3048);
        Register("yd", "yard", "Length", length, 0.9144);
        Register("fur", "furlong", "Length", length, 201.168);
        Register("mi", "mile", "Length", length, 1609.344);
        Register("nmi", "nautical mile", "Length", length, 1852.0);
        Register("au", "astronomical unit", "Length", length, 1.495978707e11);
        Register("ly", "light-year", "Length", length, 9.4607304725808e15);
        Register("pc", "parsec", "Length", length, 3.0856775814913673e16);

        // ── Mass ──────────────────────────────────────────────────
        Register("kg", "kilogram", "Mass", mass, 1.0);
        Register("g", "gram", "Mass", mass, 0.001);
        Register("lb", "pound", "Mass", mass, 0.45359237);
        Register("oz", "ounce", "Mass", mass, 0.028349523125);
        Register("st", "stone", "Mass", mass, 6.35029318);
        Register("ton", "short ton", "Mass", mass, 907.18474);
        Register("t", "metric tonne", "Mass", mass, 1000.0);

        // ── Time ──────────────────────────────────────────────────

        Register("s", "second", "Duration", time, 1.0);
        Register("jiffy", "jiffy", "Duration", time, 3 * Math.Pow(10, -24));
        Register("min", "minute", "Duration", time, 60.0);
        Register("hr", "hour", "Duration", time, 3600.0);
        Register("sol", "day", "Duration", time, 86400.0, allowSiPrefixes: true);
        Register("day", "day", "Duration", time, 86400.0, allowSiPrefixes: true);
        Register("ftnt", "fortnight", "Duration", time, 1209600);
        Register("wk", "week", "Duration", time, 604800.0);
        Register("cy", "common year", "Duration", time, 31536000);
        Register("lpy", "leap year", "Duration", time, 31622400);
        Register("ty", "tropical year", "Duration", time, 31556925.216);
        Register("jy", "julian year", "Duration", time, 31557600);
        Register("sy", "sidereal year", "Duration", time, 31558149.7635456);


        // ── Temperature ───────────────────────────────────────────
        Register("K", "kelvin", "Temperature", temperature, 1.0,
            allowSiPrefixes: true, role: UnitRole.AbsoluteTemperature);
        Register("degC", "degree Celsius", "Temperature", temperature, 1.0, 273.15,
            role: UnitRole.AbsoluteTemperature);
        Register("°C", "degree Celsius", "Temperature", temperature, 1.0, 273.15,
            role: UnitRole.AbsoluteTemperature);
        Register("degF", "degree Fahrenheit", "Temperature", temperature, 5.0 / 9.0, 459.67 * 5.0 / 9.0,
            role: UnitRole.AbsoluteTemperature);
        Register("°F", "degree Fahrenheit", "Temperature", temperature, 5.0 / 9.0, 459.67 * 5.0 / 9.0,
            role: UnitRole.AbsoluteTemperature);
        Register("degR", "degree Rankine", "Temperature", temperature, 5.0 / 9.0,
            role: UnitRole.AbsoluteTemperature);
        Register("°R", "degree Rankine", "Temperature", temperature, 5.0 / 9.0,
            role: UnitRole.AbsoluteTemperature);

        // ── Data ──────────────────────────────────────────────────
        Register("bit", "bit", "DataSize", data, 1.0);
        Register("b", "bit", "DataSize", data, 1.0);               // common alias
        Register("B", "byte", "DataSize", data, 8.0);
        Register("kB", "kilobyte", "DataSize", data, 8_000.0);
        Register("KB", "kilobyte", "DataSize", data, 8_000.0);      // common alias
        Register("kb", "kilobit", "DataSize", data, 1_000.0);
        Register("MB", "megabyte", "DataSize", data, 8_000_000.0);
        Register("Mb", "megabit", "DataSize", data, 1_000_000.0);
        Register("GB", "gigabyte", "DataSize", data, 8_000_000_000.0);
        Register("Gb", "gigabit", "DataSize", data, 1_000_000_000.0);
        Register("TB", "terabyte", "DataSize", data, 8_000_000_000_000.0);
        Register("Tb", "terabit", "DataSize", data, 1_000_000_000_000.0);
        Register("PB", "petabyte", "DataSize", data, 8_000_000_000_000_000.0);
        Register("Pb", "petabit", "DataSize", data, 1_000_000_000_000_000.0);
        Register("KiB", "kibibyte", "DataSize", data, 8.0 * 1024);
        Register("MiB", "mebibyte", "DataSize", data, 8.0 * 1024 * 1024);
        Register("GiB", "gibibyte", "DataSize", data, 8.0 * 1024 * 1024 * 1024);
        Register("TiB", "tebibyte", "DataSize", data, 8.0 * 1024 * 1024 * 1024 * 1024);
        Register("PiB", "pebibyte", "DataSize", data, 8.0 * 1024 * 1024 * 1024 * 1024 * 1024);

        // ── Area ──────────────────────────────────────────────────
        Register("ha", "hectare", "Area", area, 10_000.0);
        Register("acre", "acre", "Area", area, 4046.8564224);

        // ── Volume ────────────────────────────────────────────────
        Register("L", "liter", "Volume", volume, 0.001);
        Register("mL", "milliliter", "Volume", volume, 1e-6);
        Register("gal", "US gallon", "Volume", volume, 0.003785411784);
        Register("qt", "US quart", "Volume", volume, 0.000946352946);
        Register("pt", "US pint", "Volume", volume, 0.000473176473);
        Register("floz", "US fluid ounce", "Volume", volume, 2.95735295625e-5);
        Register("cup", "US cup", "Volume", volume, 0.000236588236);
        Register("tbsp", "tablespoon", "Volume", volume, 1.47867647813e-5);
        Register("tsp", "teaspoon", "Volume", volume, 4.92892159375e-6);

        // ── Speed ─────────────────────────────────────────────────
        Register("fps", "feet per second", "Speed", speed, 0.3048);
        Register("mphr", "meters per hour", "Speed", speed, 1.0 / 3600);
        Register("mph", "miles per hour", "Speed", speed, 0.44704);
        Register("kph", "kilometers per hour", "Speed", speed, 1.0 / 3.6);
        Register("kn", "knot", "Speed", speed, 1852.0 / 3600.0);
        Register("mach", "mach (ISA sea level)", "Speed", speed, 340.29);
        Register("AUd", "astronomical units per day", "Speed", speed, 149_597_870_700.0 / 86_400.0);
        Register("c", "× speed of light in vacuum", "Speed", speed, 299792458.0);
        Register("c_air", "× speed of light in air", "Speed", speed, 299_706_000.0);
        Register("c_water", "× speed of light in water", "Speed", speed, 224_900_000.0);
        Register("c_glass", "× speed of light in glass", "Speed", speed, 199_861_639.0);
        Register("c_diamond", "× speed of light in diamond", "Speed", speed, 123_985_000.0);
        Register("c_ice", "× speed of light in ice", "Speed", speed, 229_000_000.0);
        Register("c_acrylic", "× speed of light in acrylic", "Speed", speed, 201_200_000.0);
        Register("c_fiber", "× speed of light in optical fiber", "Speed", speed, 204_190_000.0);

        // ── Acceleration ──────────────────────────────────────────
        // m/s² is naturally composed; provide a convenience alias
        Register("gforce", "standard gravity", "Acceleration", acceleration, 9.80665);

        // ── Force ─────────────────────────────────────────────────
        Register("N", "newton", "Force", force, 1.0);
        Register("lbf", "pound-force", "Force", force, 4.4482216152605);
        Register("dyn", "dyne", "Force", force, 1e-5);

        // ── Energy ────────────────────────────────────────────────
        Register("J", "joule", "Energy", energy, 1.0);
        Register("Ws", "watt-second", "Energy", energy, 1.0);
        Register("cal", "calorie", "Energy", energy, 4.184);
        Register("kcal", "kilocalorie", "Energy", energy, 4184.0);
        Register("BTU", "British thermal unit", "Energy", energy, 1055.06);
        Register("eV", "electron volt", "Energy", energy, 1.602176634e-19);
        Register("Wh", "watt-hour", "Energy", energy, 3600.0, allowSiPrefixes: true);

        // ── Power ─────────────────────────────────────────────────
        Register("W", "watt", "Power", power, 1.0);
        Register("hp", "horsepower", "Power", power, 745.69987158227022);

        // ── Pressure ──────────────────────────────────────────────
        Register("Pa", "pascal", "Pressure", pressure, 1.0);
        Register("bar", "bar", "Pressure", pressure, 100000.0);
        Register("atm", "atmosphere", "Pressure", pressure, 101_325.0);
        Register("psi", "pound per square inch", "Pressure", pressure, 6894.757293168);
        Register("mmHg", "millimeters of mercury", "Pressure", pressure, 133.322387415);
        Register("inHg", "inches of mercury", "Pressure", pressure, 3386.38866667);
        Register("torr", "torr", "Pressure", pressure, 101325.0 / 760.0);
        Register("mbar", "millibar", "Pressure", pressure, 100.0);

        // ── Frequency ─────────────────────────────────────────────
        Register("Hz", "hertz", "Frequency", frequency, 1.0);
        Register("Bd", "baud", "Frequency", frequency, 1.0);
        Register("Bq", "becquerel", "Activity", frequency, 1.0);

        // ── Electric ──────────────────────────────────────────────
        Register("A", "ampere", "Current", current, 1.0);
        Register("C", "coulomb", "Charge", charge, 1.0);
        Register("Ah", "ampere-hour", "Charge", charge, 3600.0);
        Register("mAh", "milliampere-hour", "Charge", charge, 3.6);
        Register("V", "volt", "Voltage", voltage, 1.0);
        Register("ohm", "ohm", "Resistance", resistance, 1.0);
        Register("Ω", "ohm", "Resistance", resistance, 1.0);
        Register("F", "farad", "Capacitance", capacitance, 1.0);
        Register("H", "henry", "Inductance", inductance, 1.0);

        // ── Torque ────────────────────────────────────────────────
        Register("Nm", "newton-meter", "Torque", torque, 1.0);

        // ── Density ───────────────────────────────────────────────
        // kg/m³ is naturally composed; no convenience aliases needed initially

        // ── Flow Rate ─────────────────────────────────────────────
        Register("gpm", "gallons per minute", "FlowRate", flowRate, 0.003785411784 / 60.0);

        // ── Angle ─────────────────────────────────────────────────
        Register("rad", "radian", "Angle", angle, 1.0, allowSiPrefixes: true);
        Register("deg", "degree", "Angle", angle, Math.PI / 180.0);
        Register("grad", "gradian", "Angle", angle, Math.PI / 200.0);
        Register("°", "degree", "Angle", angle, Math.PI / 180.0);
        Register("arcmin", "arcminute", "Angle", angle, Math.PI / 10800.0);
        Register("arcsec", "arcsecond", "Angle", angle, Math.PI / 648000.0);

        // ── Amount of Substance ───────────────────────────────────
        Register("mol", "mole", "Substance", substance, 1.0);

        // ── Luminous Intensity ────────────────────────────────────
        Register("cd", "candela", "Luminosity", luminosity, 1.0);

        // ── Angular Velocity ──────────────────────────────────────
        Register("rpm", "revolutions per minute", "AngularVelocity", angularVelocity, 2.0 * Math.PI / 60.0);

        // ── Dimension → Category mapping ──────────────────────────
        _dimensionToCategory[length] = "Length";
        _dimensionToCategory[mass] = "Mass";
        _dimensionToCategory[time] = "Duration";
        _dimensionToCategory[current] = "Current";
        _dimensionToCategory[temperature] = "Temperature";
        _dimensionToCategory[substance] = "Substance";
        _dimensionToCategory[luminosity] = "Luminosity";
        _dimensionToCategory[data] = "DataSize";
        _dimensionToCategory[angle] = "Angle";
        _dimensionToCategory[area] = "Area";
        _dimensionToCategory[volume] = "Volume";
        _dimensionToCategory[speed] = "Speed";
        _dimensionToCategory[acceleration] = "Acceleration";
        _dimensionToCategory[force] = "Force";
        _dimensionToCategory[energy] = "Energy";
        _dimensionToCategory[power] = "Power";
        _dimensionToCategory[pressure] = "Pressure";
        _dimensionToCategory[frequency] = "Frequency";
        _dimensionToCategory[charge] = "Charge";
        _dimensionToCategory[voltage] = "Voltage";
        _dimensionToCategory[resistance] = "Resistance";
        _dimensionToCategory[capacitance] = "Capacitance";
        _dimensionToCategory[inductance] = "Inductance";
        _dimensionToCategory[density] = "Density";
        _dimensionToCategory[flowRate] = "FlowRate";
        _dimensionToCategory[angularVelocity] = "AngularVelocity";
    }

    private void RegisterNamedTypeFactories()
    {
        var length = UnitExpression.Of(UnitDimension.Length);
        var mass = UnitExpression.Of(UnitDimension.Mass);
        var time = UnitExpression.Of(UnitDimension.Time);
        var temperature = UnitExpression.Of(UnitDimension.Temperature);
        var data = UnitExpression.Of(UnitDimension.Data);
        var speed = UnitExpression.Of((UnitDimension.Length, 1), (UnitDimension.Time, -1));
        var area = UnitExpression.Of(UnitDimension.Length, 2);
        var volume = UnitExpression.Of(UnitDimension.Length, 3);
        var force = UnitExpression.Of((UnitDimension.Mass, 1), (UnitDimension.Length, 1), (UnitDimension.Time, -2));
        var energy = UnitExpression.Of((UnitDimension.Mass, 1), (UnitDimension.Length, 2), (UnitDimension.Time, -2));
        var power = UnitExpression.Of((UnitDimension.Mass, 1), (UnitDimension.Length, 2), (UnitDimension.Time, -3));
        var pressure = UnitExpression.Of((UnitDimension.Mass, 1), (UnitDimension.Length, -1), (UnitDimension.Time, -2));
        var frequency = UnitExpression.Of(UnitDimension.Time, -1);
        var angle = UnitExpression.Of(UnitDimension.Angle);
        var acceleration = UnitExpression.Of((UnitDimension.Length, 1), (UnitDimension.Time, -2));
        var density = UnitExpression.Of((UnitDimension.Mass, 1), (UnitDimension.Length, -3));
        var voltage = UnitExpression.Of((UnitDimension.Mass, 1), (UnitDimension.Length, 2), (UnitDimension.Time, -3), (UnitDimension.ElectricCurrent, -1));
        var current = UnitExpression.Of(UnitDimension.ElectricCurrent);
        var resistance = UnitExpression.Of((UnitDimension.Mass, 1), (UnitDimension.Length, 2), (UnitDimension.Time, -3), (UnitDimension.ElectricCurrent, -2));
        var charge = UnitExpression.Of((UnitDimension.ElectricCurrent, 1), (UnitDimension.Time, 1));
        var flowRate = UnitExpression.Of((UnitDimension.Length, 3), (UnitDimension.Time, -1));
        var capacitance = UnitExpression.Of((UnitDimension.ElectricCurrent, 2), (UnitDimension.Time, 4), (UnitDimension.Mass, -1), (UnitDimension.Length, -2));
        var inductance = UnitExpression.Of((UnitDimension.Mass, 1), (UnitDimension.Length, 2), (UnitDimension.Time, -2), (UnitDimension.ElectricCurrent, -2));
        var substance = UnitExpression.Of(UnitDimension.AmountOfSubstance);
        var luminosity = UnitExpression.Of(UnitDimension.LuminousIntensity);
        var angularVelocity = UnitExpression.Of((UnitDimension.Angle, 1), (UnitDimension.Time, -1));

        _namedTypeFactories[length] = (mag, sym) => new LengthQuantity(mag, sym);
        _namedTypeFactories[mass] = (mag, sym) => new MassQuantity(mag, sym);
        _namedTypeFactories[time] = (mag, sym) => new DurationQuantity(mag, sym);
        _namedTypeFactories[temperature] = (mag, sym) => new TemperatureQuantity(mag, sym);
        _namedTypeFactories[data] = (mag, sym) => new DataSizeQuantity(mag, sym);
        _namedTypeFactories[speed] = (mag, sym) => new SpeedQuantity(mag, sym);
        _namedTypeFactories[area] = (mag, sym) => new AreaQuantity(mag, sym);
        _namedTypeFactories[volume] = (mag, sym) => new VolumeQuantity(mag, sym);
        _namedTypeFactories[force] = (mag, sym) => new ForceQuantity(mag, sym);
        _namedTypeFactories[energy] = (mag, sym) => new EnergyQuantity(mag, sym);
        _namedTypeFactories[power] = (mag, sym) => new PowerQuantity(mag, sym);
        _namedTypeFactories[pressure] = (mag, sym) => new PressureQuantity(mag, sym);
        _namedTypeFactories[frequency] = (mag, sym) => new FrequencyQuantity(mag, sym);
        _namedTypeFactories[angle] = (mag, sym) => new AngleQuantity(mag, sym);
        _namedTypeFactories[acceleration] = (mag, sym) => new AccelerationQuantity(mag, sym);
        _namedTypeFactories[density] = (mag, sym) => new DensityQuantity(mag, sym);
        _namedTypeFactories[voltage] = (mag, sym) => new VoltageQuantity(mag, sym);
        _namedTypeFactories[current] = (mag, sym) => new CurrentQuantity(mag, sym);
        _namedTypeFactories[resistance] = (mag, sym) => new ResistanceQuantity(mag, sym);
        _namedTypeFactories[charge] = (mag, sym) => new ChargeQuantity(mag, sym);
        _namedTypeFactories[flowRate] = (mag, sym) => new FlowRateQuantity(mag, sym);
        _namedTypeFactories[capacitance] = (mag, sym) => new CapacitanceQuantity(mag, sym);
        _namedTypeFactories[inductance] = (mag, sym) => new InductanceQuantity(mag, sym);
        _namedTypeFactories[substance] = (mag, sym) => new SubstanceQuantity(mag, sym);
        _namedTypeFactories[luminosity] = (mag, sym) => new LuminosityQuantity(mag, sym);
        _namedTypeFactories[angularVelocity] = (mag, sym) => new AngularVelocityQuantity(mag, sym);

        _categoryTypeFactories["Length"] = (mag, sym) => new LengthQuantity(mag, sym);
        _categoryTypeFactories["Mass"] = (mag, sym) => new MassQuantity(mag, sym);
        _categoryTypeFactories["Duration"] = (mag, sym) => new DurationQuantity(mag, sym);
        _categoryTypeFactories["Temperature"] = (mag, sym) => new TemperatureQuantity(mag, sym);
        _categoryTypeFactories["DataSize"] = (mag, sym) => new DataSizeQuantity(mag, sym);
        _categoryTypeFactories["Speed"] = (mag, sym) => new SpeedQuantity(mag, sym);
        _categoryTypeFactories["Area"] = (mag, sym) => new AreaQuantity(mag, sym);
        _categoryTypeFactories["Volume"] = (mag, sym) => new VolumeQuantity(mag, sym);
        _categoryTypeFactories["Force"] = (mag, sym) => new ForceQuantity(mag, sym);
        _categoryTypeFactories["Energy"] = (mag, sym) => new EnergyQuantity(mag, sym);
        _categoryTypeFactories["Power"] = (mag, sym) => new PowerQuantity(mag, sym);
        _categoryTypeFactories["Pressure"] = (mag, sym) => new PressureQuantity(mag, sym);
        _categoryTypeFactories["Frequency"] = (mag, sym) => new FrequencyQuantity(mag, sym);
        _categoryTypeFactories["Angle"] = (mag, sym) => new AngleQuantity(mag, sym);
        _categoryTypeFactories["Acceleration"] = (mag, sym) => new AccelerationQuantity(mag, sym);
        _categoryTypeFactories["Density"] = (mag, sym) => new DensityQuantity(mag, sym);
        _categoryTypeFactories["Voltage"] = (mag, sym) => new VoltageQuantity(mag, sym);
        _categoryTypeFactories["Current"] = (mag, sym) => new CurrentQuantity(mag, sym);
        _categoryTypeFactories["Resistance"] = (mag, sym) => new ResistanceQuantity(mag, sym);
        _categoryTypeFactories["Charge"] = (mag, sym) => new ChargeQuantity(mag, sym);
        _categoryTypeFactories["Torque"] = (mag, sym) => new TorqueQuantity(mag, sym);
        _categoryTypeFactories["FlowRate"] = (mag, sym) => new FlowRateQuantity(mag, sym);
        _categoryTypeFactories["Capacitance"] = (mag, sym) => new CapacitanceQuantity(mag, sym);
        _categoryTypeFactories["Inductance"] = (mag, sym) => new InductanceQuantity(mag, sym);
        _categoryTypeFactories["Substance"] = (mag, sym) => new SubstanceQuantity(mag, sym);
        _categoryTypeFactories["Luminosity"] = (mag, sym) => new LuminosityQuantity(mag, sym);
        _categoryTypeFactories["AngularVelocity"] = (mag, sym) => new AngularVelocityQuantity(mag, sym);
    }

    private void Register(
        string symbol,
        string name,
        string category,
        UnitExpression dimension,
        double factor,
        double offset = 0.0,
        bool? allowSiPrefixes = null,
        UnitRole role = UnitRole.Linear)
    {
        _units[symbol] = new UnitDefinition(
            symbol,
            name,
            category,
            dimension,
            factor,
            offset,
            allowSiPrefixes: allowSiPrefixes ?? IsSiPrefixBaseSymbol(symbol),
            role: role);

        // The first exact base-scale unit registered for a dimension is its
        // canonical display. Registration order deliberately puts J before Nm,
        // preserving energy as the canonical result for dimension-only arithmetic.
        if (factor == 1.0 && offset == 0.0)
        {
            _dimensionToCanonicalUnit.TryAdd(dimension, symbol);
        }
    }

    private static bool IsSiPrefixBaseSymbol(string symbol) => symbol is
        "m" or "g" or "s" or "A" or "mol" or "cd" or "bit" or "B" or
        "L" or "N" or "J" or "W" or "Pa" or "Hz" or "C" or "V" or
        "ohm" or "Ω" or "F" or "H";

    #endregion
}
