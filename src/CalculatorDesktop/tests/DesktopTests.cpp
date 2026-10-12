// Licensed under the MIT License.
#include "AboutInfo.h"
#include "DesktopModel.h"
#include <algorithm>
#include <iostream>
#include <stdexcept>

using CalculationManager::Command;
using Desktop::DesktopModel;
using Desktop::Mode;

static void Require(bool condition, const char* message)
{
    if (!condition)
    {
        throw std::runtime_error(message);
    }
}

static void Type(DesktopModel& model, std::wstring_view value)
{
    for (const wchar_t key : value)
    {
        Require(model.PressKey(key), "Keyboard command missing");
    }
}

static void TestScientificPresentation()
{
    const std::vector<std::vector<Command>> expected = {
        {Command::CommandINV, Command::CommandPI, Command::CommandEuler, Command::CommandCLEAR, Command::CommandBACK},
        {Command::CommandSQR, Command::CommandREC, Command::CommandAbs, Command::CommandEXP, Command::CommandMOD},
        {Command::CommandSQRT, Command::CommandOPENP, Command::CommandCLOSEP, Command::CommandFAC, Command::CommandDIV},
        {Command::CommandPWR, Command::Command7, Command::Command8, Command::Command9, Command::CommandMUL},
        {Command::CommandPOW10, Command::Command4, Command::Command5, Command::Command6, Command::CommandSUB},
        {Command::CommandLOG, Command::Command1, Command::Command2, Command::Command3, Command::CommandADD},
        {Command::CommandLN, Command::CommandSIGN, Command::Command0, Command::CommandPNT, Command::CommandEQU}};
    const auto rows = Desktop::BuildButtonRows(Mode::Scientific, L',');
    Require(rows.size() == expected.size(), "Scientific keypad must have seven rows");
    for (size_t row = 0; row < rows.size(); ++row)
    {
        Require(rows[row].size() == expected[row].size(), "Scientific keypad must have five columns");
        for (size_t column = 0; column < rows[row].size(); ++column)
            Require(rows[row][column].command == expected[row][column], "Scientific keypad must follow the original command arrangement");
    }
    Require(rows[6][3].title == L",", "Scientific decimal key must follow the locale");
    const auto shifted = Desktop::BuildButtonRows(Mode::Scientific, L'.', true);
    const std::vector<Command> alternate = {Command::CommandCUB,  Command::CommandCUBEROOT, Command::CommandROOT,
                                            Command::CommandPOW2, Command::CommandLogBaseY, Command::CommandPOWE};
    for (size_t row = 1; row < shifted.size(); ++row)
    {
        Require(shifted[row][0].command == alternate[row - 1], "Second functions must use explicit engine commands");
        for (size_t column = 1; column < shifted[row].size(); ++column)
            Require(shifted[row][column].command == rows[row][column].command,
                    "Second functions must only replace the first operator column");
    }
    const auto menus = Desktop::BuildScientificMenus();
    Require(menus.back().buttons.size() == 6 && menus.back().buttons.front().command == Command::CommandAbs,
            "Function popup must contain the original six functions");

    DesktopModel model;
    model.SetMode(Mode::Scientific);
    model.PasteNumber(L"8");
    const auto before = model.GetDisplay().primary;
    model.SendCommand(Command::CommandINV);
    Require(model.GetScientificShift() && model.GetDisplay().primary == before, "Second functions must preserve calculation state");
    model.SendCommand(Command::CommandSQR);
    Require(model.GetDisplay().primary == L"64", "Second functions must not toggle core unary inversion");
    model.PasteNumber(L"8");
    model.SendCommand(Command::CommandCUBEROOT);
    Require(model.GetDisplay().primary == L"2" && !model.GetScientificShift(),
            "Alternate operator must calculate and restore first functions");
    model.SendCommand(Command::CommandFE);
    Require(model.GetScientificFormat(), "F-E must expose its selected state");
    model.SetMode(Mode::Standard);
    Require(!model.IsCommandEnabled(Command::CommandFE) && !model.IsCommandEnabled(Command::CommandINV),
            "Scientific presentation commands must be disabled outside scientific mode");
    model.SetMode(Mode::Scientific);
    Require(model.GetScientificFormat(), "Scientific notation must track the reused engine across mode changes");
    model.SendCommand(Command::CommandFE);
    Require(!model.GetScientificFormat(), "F-E must restore decimal display state");
    model.PasteNumber(L"0");
    model.SendCommand(Command::CommandREC);
    Require(model.GetDisplay().is_error && !model.IsCommandEnabled(Command::CommandFE),
            "F-E must be disabled when the engine rejects operations");
    model.SendCommand(Command::CommandFE);
    Require(!model.GetScientificFormat(), "A rejected F-E command must preserve its selected state");
    model.SendCommand(Command::CommandCLEAR);
    model.SendCommand(Command::CommandINV);
    DesktopModel other;
    other.SetMode(Mode::Scientific);
    Require(!other.GetScientificShift() && !other.GetScientificFormat(), "Scientific presentation state must belong to each model");
    model.SetMode(Mode::Standard);
    model.SetMode(Mode::Scientific);
    Require(!model.GetScientificShift(), "Entering scientific mode must restore first functions");
}

static void TestPasteShortcutsAndHistory()
{
    DesktopModel model;
    model.SetMode(Mode::Scientific);
    Require(model.PasteNumber(L"-1.25e-2="), "Signed exponent paste failed");
    Require(model.GetDisplay().primary == L"-0.0125", "Exponent signs must not change the mantissa sign");
    model.SendCommand(Command::CommandCLEAR);
    Require(model.PasteNumber(L"(2 + 3) * 4="), "Scientific expression paste failed");
    Require(model.GetDisplay().primary == L"20", "Expression paste must use scientific precedence");
    const auto expression = model.GetDisplay().expression;
    const auto primary = model.GetDisplay().primary;
    const auto historySize = model.GetDisplay().history.size();
    for (const auto invalid : {L"2+", L"1e", L"1e-", L"(2+3", L"2**3", L"12;broken", L"1e12345"})
    {
        Require(!model.PasteNumber(invalid), "Malformed paste must fail before execution");
        Require(model.GetDisplay().primary == primary && model.GetDisplay().expression == expression &&
                    model.GetDisplay().history.size() == historySize,
                "Malformed paste must preserve calculator state");
    }
    model.SendCommand(Command::CommandCLEAR);
    Type(model, L"2^3=");
    Require(model.GetDisplay().primary == L"8", "Scientific power shortcut failed");
    model.SendCommand(Command::CommandCLEAR);
    Type(model, L"7%4=");
    Require(model.GetDisplay().primary == L"3", "Scientific percent shortcut must select Mod");
    model.SendCommand(Command::CommandCLEAR);
    Type(model, L"9@");
    Require(model.GetDisplay().primary == L"3", "Original square-root shortcut failed");

    model.SetMode(Mode::Programmer);
    model.SendCommand(Command::CommandHex);
    model.SendCommand(Command::CommandByte);
    Require(model.PasteNumber(L"0x7FuLL") && model.GetDisplay().primary == L"7F", "Hex prefix and integer suffix paste failed");
    Require(!model.PasteNumber(L"0x100") && model.GetDisplay().primary == L"7F", "Programmer paste must respect word width");
    Require(!model.PasteNumber(L"0b11") && model.GetDisplay().primary == L"7F", "A prefix must match the selected radix");
    model.SendCommand(Command::CommandBin);
    Require(model.PasteNumber(L"0b0111_1111"), "Binary prefix and digit separator paste failed");
    for (unsigned bit = 0; bit < 8; ++bit)
        Require(model.IsBitSet(bit) == (bit < 7), "Binary prefix paste must preserve all eight bits of the input value");
    model.SendCommand(Command::CommandDec);
    model.SendCommand(Command::CommandCLEAR);
    Type(model, L"7^3=");
    Require(model.GetDisplay().primary == L"4", "Programmer caret shortcut must select XOR");
    model.SendCommand(Command::CommandCLEAR);
    Type(model, L"7%4=");
    Require(model.GetDisplay().primary == L"3", "Programmer percent shortcut must select Mod");
    model.SendCommand(Command::CommandCLEAR);
    Type(model, L"1/0=");
    Require(model.GetDisplay().is_error, "Radix recovery setup must produce an error");
    model.SendCommand(Command::CommandHex);
    Require(!model.GetDisplay().is_error && model.GetRadix() == 16 && model.GetDisplay().primary == L"0",
            "Radix selection must clear the preceding error");

    model.SetMode(Mode::Standard);
    model.ClearHistory();
    Type(model, L"2+3=");
    const auto savedExpression = model.GetDisplay().expression;
    Type(model, L"7+8=");
    const auto item = std::find_if(model.GetDisplay().history.begin(), model.GetDisplay().history.end(),
                                   [](const Desktop::HistoryItem& history) { return history.result == L"5"; });
    Require(item != model.GetDisplay().history.end(), "History replay setup failed");
    const size_t count = model.GetDisplay().history.size();
    model.RecallHistory(static_cast<size_t>(item - model.GetDisplay().history.begin()));
    Require(model.GetDisplay().primary == L"5" && model.GetDisplay().expression == savedExpression &&
                model.GetDisplay().history.size() == count,
            "History recall must restore the stored expression without adding a duplicate");
    model.SendCommand(Command::CommandEQU);
    Require(model.GetDisplay().primary == L"5", "Recalled history must retain the stored pending calculation");
    model.SendCommand(Command::CommandEQU);
    Require(model.GetDisplay().primary == L"8", "Repeated Equals after history replay must retain the original operand");
    model.SendCommand(Command::CommandCLEAR);
    Require(model.PasteNumber(L"1.5e2=") && model.GetDisplay().primary == L"150", "Standard exponential paste failed");
    model.SetMode(Mode::Scientific);
    model.ClearHistory();
    Require(model.PasteNumber(L"(-2+3)^3="), "Scientific history setup failed");
    const auto scientificExpression = model.GetDisplay().expression;
    model.PasteNumber(L"9");
    model.RecallHistory(0);
    Require(model.GetDisplay().primary == L"1" && model.GetDisplay().expression == scientificExpression,
            "History replay must preserve parentheses, operand signs, and scientific operators");
    Type(model, L"+2=");
    Require(model.GetDisplay().primary == L"3", "An operation after scientific history recall must use its restored expression");
    model.SetMode(Mode::Programmer);
    model.SendCommand(Command::CommandOct);
    Require(model.PasteNumber(L"0t17") && model.IsBitSet(3) && model.IsBitSet(0), "Original octal prefix alias failed");
}

static void TestFreshLocalizedModels()
{
    // A fresh engine's input buffer must use its locale even when heap storage comes from the same locale.
    for (int iteration = 0; iteration < 8; ++iteration)
    {
        DesktopModel localized("pt-BR", {L",", L".", L"3;0"}, "BR");
        Require(localized.PasteNumber(L"1,5") && !localized.GetDisplay().is_error && localized.GetDisplay().primary == L"1,5",
                "A recreated localized model must initialize its decimal input separator");
    }
}

static void TestFrontendCommands()
{
    DesktopModel model;
    model.SetMode(Mode::Scientific);
    model.PasteNumber(L"8");
    model.SendCommand(Command::CommandCUBEROOT);
    Require(model.GetDisplay().primary == L"2", "Cube root must use the shared engine");
    model.PasteNumber(L"5");
    model.SendCommand(Command::CommandPOW2);
    Require(model.GetDisplay().primary == L"32", "Two to the power of x failed");
    model.PasteNumber(L"-1.2");
    model.SendCommand(Command::CommandFloor);
    Require(model.GetDisplay().primary == L"-2", "Floor must round negative numbers downward");
    model.PasteNumber(L"-1.2");
    model.SendCommand(Command::CommandCeil);
    Require(model.GetDisplay().primary == L"-1", "Ceiling must round negative numbers upward");
    model.PasteNumber(L"0");
    model.SendCommand(Command::CommandASINH);
    Require(model.GetDisplay().primary == L"0", "Inverse hyperbolic sine failed");
    model.PasteNumber(L"0");
    model.SendCommand(Command::CommandSEC);
    Require(model.GetDisplay().primary == L"1", "Secant failed");
    model.SendCommand(Command::CommandCLEAR);
    model.PasteNumber(L"8");
    model.SendCommand(Command::CommandLogBaseY);
    Type(model, L"2=");
    Require(model.GetDisplay().primary == L"3", "Logarithm with a selected base failed");
    model.SendCommand(Command::CommandRAD);
    model.SetMode(Mode::Standard);
    model.SetMode(Mode::Scientific);
    Require(model.GetAngleCommand() == Command::CommandRAD, "Scientific angle selection must survive mode changes");
    model.PasteNumber(L"1");
    model.SendCommand(Command::CommandCOS);
    Require(model.GetDisplay().primary.find(L"0.540302305868") == 0, "The scientific engine must use the retained radian selection");

    model.SetMode(Mode::Standard);
    model.PasteNumber(L"10");
    model.SendCommand(Command::CommandSTORE);
    model.PasteNumber(L"20");
    model.SendCommand(Command::CommandSTORE);
    model.PasteNumber(L"3");
    model.AddToMemory(1);
    Require(model.GetDisplay().memory[0] == L"20" && model.GetDisplay().memory[1] == L"13",
            "Adding to a memory slot must preserve other slots");
    model.SubtractFromMemory(1);
    Require(model.GetDisplay().memory[1] == L"10", "Subtracting from a selected memory slot failed");
    model.AddToMemory(99);
    model.SubtractFromMemory(99);
    model.RemoveMemory(99);
    Require(model.GetDisplay().memory.size() == 2, "Out-of-range memory actions must preserve slots");
    model.RemoveMemory(0);
    Require(model.GetDisplay().memory.size() == 1 && model.GetDisplay().memory[0] == L"10",
            "Removing a memory slot must refresh and reindex the remaining slots");
    model.RecallMemory(0);
    Require(model.GetDisplay().memory.size() == 1 && model.GetDisplay().primary == L"10", "Removing a memory slot failed");
    model.RemoveMemory(0);
    Require(model.GetDisplay().memory.empty() && !model.IsCommandEnabled(Command::CommandRECALL),
            "Removing the last memory slot must disable recall");
    model.RecallMemory(0);
    model.RemoveMemory(0);
    Require(model.GetDisplay().primary == L"10" && model.GetDisplay().memory.empty(),
            "Memory actions after the last removal must preserve the display");

    model.SendCommand(Command::CommandCLEAR);
    model.ClearHistory();
    Type(model, L"2+3=");
    Type(model, L"7+8=");
    const auto historySize = model.GetDisplay().history.size();
    model.RecallHistory(0);
    Require(model.GetDisplay().primary == model.GetDisplay().history[0].result, "History result recall failed");
    Require(model.GetDisplay().history.size() == historySize, "History recall must not add a duplicate entry");
    model.RemoveHistory(99);
    Require(model.GetDisplay().history.size() == historySize, "Out-of-range history removal must preserve entries");
    model.RemoveHistory(0);
    Require(model.GetDisplay().history.size() == historySize - 1, "History entry removal failed");

    model.SetMode(Mode::Programmer);
    model.SendCommand(Command::CommandBINPOS63);
    Require(model.IsBitSet(63) && !model.IsBitSet(62), "The highest QWORD bit must be readable without signed overflow");
    model.SendCommand(Command::CommandCLEAR);
    model.SendCommand(Command::CommandByte);
    model.SendCommand(Command::CommandBINPOS7);
    Require(model.GetWordWidth() == 8 && model.IsBitSet(7), "The highest BYTE bit must be available");
    Require(!model.IsCommandEnabled(Command::CommandBINPOS8) && !model.IsBitSet(8), "Bits above the selected width must be disabled");
    const auto byteValue = model.GetDisplay().primary;
    model.SendCommand(Command::CommandBINPOS8);
    Require(model.GetDisplay().primary == byteValue, "Disabled bit commands must preserve the value");
    model.SendCommand(Command::CommandHex);
    Require(model.GetRadix() == 16, "Radix state must follow engine commands");
    model.SetMode(Mode::Standard);
    model.SetMode(Mode::Programmer);
    Require(model.GetRadix() == 10 && model.GetWordWidth() == 8, "Mode changes must reset radix and retain programmer word width");
    model.SendCommand(Command::CommandBINPOS7);
    const auto valueBeforeDate = model.GetDisplay().primary;
    model.SetMode(Mode::Date);
    model.SendCommand(Command::CommandCLEAR);
    Require(model.GetDisplay().primary == valueBeforeDate, "Date mode must leave calculator engines unchanged");
}

int main()
{
    try
    {
        TestScientificPresentation();
        TestPasteShortcutsAndHistory();
        TestFreshLocalizedModels();
        TestFrontendCommands();
        const auto about = Desktop::GetAboutInfo();
        Require(std::count(about.version.begin(), about.version.end(), '.') == 3,
                "About must retain all four Windows package version components");
        Require(!about.copyright.empty() && about.license.find(about.copyright) == 0, "About copyright must match the repository license");
        Require(about.license.find("MIT License") != std::string_view::npos &&
                    about.license.find("Permission is hereby granted") != std::string_view::npos &&
                    about.license.find("THE SOFTWARE IS PROVIDED *AS IS*") != std::string_view::npos,
                "About must include the MIT permissions and warranty disclaimer");
        Require(about.source_url == "https://github.com/microsoft/calculator", "About must identify the Microsoft calculator source");
        Require(about.privacy_url == "https://go.microsoft.com/fwlink/?LinkID=521839" &&
                    about.services_url == "https://go.microsoft.com/fwlink/?LinkID=822631",
                "About must preserve the Microsoft Settings policy links");

        DesktopModel model;
        Type(model, L"2+3*4=");
        Require(model.GetDisplay().primary == L"20", "Standard mode must evaluate immediately");
        Require(!model.GetDisplay().history.empty() && model.GetDisplay().history.back().result == L"20",
                "History must receive the engine result");
        model.SendCommand(Command::CommandSTORE);
        model.SendCommand(Command::CommandCLEAR);
        model.SendCommand(Command::CommandRECALL);
        Require(model.GetDisplay().primary == L"20", "Memory recall failed");

        model.SetMode(Mode::Scientific);
        Type(model, L"2+3*4=");
        Require(model.GetDisplay().primary == L"14", "Scientific mode must respect precedence");
        model.SendCommand(Command::CommandCLEAR);
        Type(model, L"(2+3)*4=");
        Require(model.GetDisplay().primary == L"20", "Scientific parentheses failed");
        model.SendCommand(Command::CommandCLEAR);
        Type(model, L"9");
        model.SendCommand(Command::CommandSQRT);
        Require(model.GetDisplay().primary == L"3", "Square root failed");
        model.SendCommand(Command::CommandCLEAR);
        Type(model, L"30");
        model.SendCommand(Command::CommandSIN);
        Require(model.GetDisplay().primary == L"0.5", "Degree trigonometry failed");

        model.SetMode(Mode::Programmer);
        model.SendCommand(Command::CommandHex);
        Type(model, L"FF");
        model.SendCommand(Command::CommandDec);
        Require(model.GetDisplay().primary == L"255", "Hexadecimal conversion failed");
        model.SendCommand(Command::CommandBin);
        Require(!model.IsCommandEnabled(Command::Command2), "Binary mode must disable digits above 1");
        Require(!model.IsCommandEnabled(Command::CommandPNT), "Programmer mode must disable decimals");
        model.SendCommand(Command::CommandDec);
        model.SendCommand(Command::CommandCLEAR);
        Type(model, L"5");
        model.SendCommand(Command::CommandAnd);
        Type(model, L"3=");
        Require(model.GetDisplay().primary == L"1", "Bitwise AND failed");

        model.SetMode(Mode::Standard);
        Type(model, L"1/0=");
        Require(model.GetDisplay().is_error, "Division by zero must reach the display");
        model.SendCommand(Command::CommandCLEAR);
        Require(!model.GetDisplay().is_error, "Clear must recover from an engine error");
        Require(model.PasteNumber(L"-12.5"), "Numeric paste failed");
        const auto before = model.GetDisplay().primary;
        Require(!model.PasteNumber(L"12;broken"), "Invalid paste must be rejected");
        Require(model.GetDisplay().primary == before, "Invalid paste must preserve the current input");

        model.SetMode(Mode::Converter);
        Require(model.GetCategories().size() == 13 && model.GetCategories().back().id == Desktop::CURRENCY_CATEGORY_ID,
                "All static converters and the provider-backed Currency category must be available");
        for (size_t i = 0; i + 1 < model.GetCategories().size(); ++i)
        {
            model.SetCategory(i);
            Require(!model.GetUnits().empty(), "A converter category has no units");
            Require(model.GetFromUnitIndex() < model.GetUnits().size(), "Default source unit missing");
            Require(model.GetToUnitIndex() < model.GetUnits().size(), "Default target unit missing");
        }
        model.SetCategory(1); // Length follows Volume in the source manifest.
        size_t meters = 0, feet = 0;
        for (size_t i = 0; i < model.GetUnits().size(); ++i)
        {
            if (model.GetUnits()[i].name == L"Meters")
            {
                meters = i;
            }
            if (model.GetUnits()[i].name == L"Feet")
            {
                feet = i;
            }
        }
        model.SetUnits(meters, feet);
        Require(model.PasteNumber(L"1"), "Converter input failed");
        Require(model.GetDisplay().converted.find(L"3.280") == 0, "Length conversion failed");
        model.SetCategory(3); // Temperature.
        size_t celsius = 0, fahrenheit = 0;
        for (size_t i = 0; i < model.GetUnits().size(); ++i)
        {
            if (model.GetUnits()[i].name == L"Celsius")
            {
                celsius = i;
            }
            if (model.GetUnits()[i].name == L"Fahrenheit")
            {
                fahrenheit = i;
            }
        }
        model.SetUnits(celsius, fahrenheit);
        model.PasteNumber(L"100");
        Require(model.GetDisplay().converted == L"212", "Temperature offsets failed");

        Desktop::ResourceProvider resources;
        auto loader = std::make_shared<Desktop::UnitDataLoader>(resources);
        auto converter = std::make_shared<UnitConversionManager::UnitConverter>(loader);
        const auto refresh = converter->RefreshCurrencyRatios().get();
        Require(!refresh.first && refresh.second.empty(), "Unavailable currency must not claim fresh data");

        DesktopModel localized("pt-BR", {L",", L".", L"3;0"}, "BR");
        Type(localized, L"1,5+2,5=");
        Require(localized.GetDisplay().primary == L"4", "Native decimal separator failed");
        Require(localized.PasteNumber(L"1.234,5"), "Localized numeric paste failed");
        Require(localized.GetDisplay().primary == L"1.234,5", "Localized grouping failed");
        localized.SetMode(Mode::Converter);
        Require(localized.PasteNumber(L"1,5"), "Localized converter input failed");
        Require(localized.GetDisplay().primary == L"1,5", "Converter must use the native decimal separator");
        std::cout << "Engine modes, precision paths, history, memory, errors, paste, locales, and conversions passed\n";
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
