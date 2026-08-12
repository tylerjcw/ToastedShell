namespace Tosh.Runtime.Units;

/// <summary>
/// The seven SI base dimensions plus two extras for data and angle.
/// Every unit ultimately decomposes into a product of these raised to integer exponents.
/// </summary>
public enum UnitDimension
{
    Length,            // meter (m)
    Mass,              // kilogram (kg)
    Time,              // second (s)
    ElectricCurrent,   // ampere (A)
    Temperature,       // kelvin (K)
    AmountOfSubstance, // mole (mol)
    LuminousIntensity, // candela (cd)
    Data,              // bit (bit)
    Angle,             // radian (rad)
}
