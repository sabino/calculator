// Licensed under the MIT License.
#include "DesktopModel.h"
#include "BridgeJson.h"
#include "CalculatorHistory.h"
#include "CalculatorManager.h"
#include <algorithm>
#include <cwctype>
#include <limits>

namespace Desktop
{
    using CalculationManager::Command;

    namespace
    {
        class ConverterDisplay final : public UnitConversionManager::IUnitConverterVMCallback
        {
          public:
            ConverterDisplay(DisplayState& display, wchar_t decimal) : m_display(display), m_decimal(decimal)
            {
            }
            void DisplayCallback(const std::wstring& from, const std::wstring& to) override
            {
                m_display.primary = from;
                m_display.converted = to;
                std::replace(m_display.primary.begin(), m_display.primary.end(), L'.', m_decimal);
                std::replace(m_display.converted.begin(), m_display.converted.end(), L'.', m_decimal);
            }
            void SuggestedValueCallback(const std::vector<std::tuple<std::wstring, UnitConversionManager::Unit>>&) override
            {
            }
            void MaxDigitsReached() override
            {
                m_display.notice = L"Maximum digits reached";
            }

          private:
            DisplayState& m_display;
            wchar_t m_decimal;
        };

        struct PasteCursor
        {
            std::wstring_view input;
            Mode mode;
            uint32_t radix;
            uint32_t width;
            wchar_t decimal;
            size_t position = 0;
            size_t operands = 0;
            std::vector<Command> commands;
        };

        void SkipPasteSpace(PasteCursor& cursor)
        {
            while (cursor.position < cursor.input.size() && std::iswspace(cursor.input[cursor.position]))
                ++cursor.position;
        }

        int ReadDigit(wchar_t key)
        {
            key = std::towupper(key);
            return key >= L'0' && key <= L'9' ? key - L'0' : key >= L'A' && key <= L'F' ? key - L'A' + 10 : -1;
        }

        bool ParsePasteNumber(PasteCursor& cursor, bool negative)
        {
            const bool programmer = cursor.mode == Mode::Programmer;
            if (++cursor.operands > 100)
                return false;
            if (programmer && cursor.position + 1 < cursor.input.size() && cursor.input[cursor.position] == L'0')
            {
                const wchar_t prefix = std::towlower(cursor.input[cursor.position + 1]);
                const uint32_t base = prefix == L'x'                     ? 16
                                      : prefix == L'n'                   ? 10
                                      : prefix == L'o' || prefix == L't' ? 8
                                      : prefix == L'b' || prefix == L'y' ? 2
                                                                         : 0;
                if (base != 0)
                {
                    if (base == cursor.radix)
                        cursor.position += 2;
                    else if (ReadDigit(prefix) < 0 || static_cast<uint32_t>(ReadDigit(prefix)) >= cursor.radix)
                        return false;
                }
            }
            size_t digits = 0;
            bool decimal = false;
            bool separator = false;
            uint64_t integer = 0;
            const uint64_t maximum = cursor.width == 64 ? std::numeric_limits<uint64_t>::max() : (uint64_t{1} << cursor.width) - 1;
            while (cursor.position < cursor.input.size())
            {
                const wchar_t key = cursor.input[cursor.position];
                const int digit = ReadDigit(key);
                if (digit >= 0 && static_cast<uint32_t>(digit) < (programmer ? cursor.radix : 10))
                {
                    if (programmer)
                    {
                        if (integer > (maximum - digit) / cursor.radix)
                            return false;
                        integer = integer * cursor.radix + digit;
                    }
                    else if (digits >= (cursor.mode == Mode::Scientific ? 32u : 16u))
                        return false;
                    cursor.commands.push_back(static_cast<Command>(static_cast<int>(Command::Command0) + digit));
                    ++digits;
                    ++cursor.position;
                    separator = false;
                }
                else if (!programmer && key == cursor.decimal && !decimal)
                {
                    cursor.commands.push_back(Command::CommandPNT);
                    decimal = true;
                    ++cursor.position;
                }
                else if (programmer && digits != 0 && !separator && (key == L'_' || key == L'\'' || key == L'`'))
                {
                    separator = true;
                    ++cursor.position;
                }
                else
                    break;
            }
            if (digits == 0 || separator)
                return false;
            if (negative)
                cursor.commands.push_back(Command::CommandSIGN);
            if (programmer)
            {
                if (cursor.position < cursor.input.size())
                {
                    const wchar_t suffix = std::towlower(cursor.input[cursor.position]);
                    if ((cursor.radix == 16 && suffix == L'h') || (cursor.radix == 2 && suffix == L'b'))
                        ++cursor.position;
                    else
                    {
                        if (suffix == L'u')
                            ++cursor.position;
                        for (int length = 0;
                             length < 2 && cursor.position < cursor.input.size() && std::towlower(cursor.input[cursor.position]) == L'l';
                             ++length)
                            ++cursor.position;
                    }
                }
                return true;
            }
            if (cursor.position < cursor.input.size() && std::towlower(cursor.input[cursor.position]) == L'e')
            {
                if (cursor.mode == Mode::Converter)
                    return false;
                cursor.commands.push_back(Command::CommandEXP);
                ++cursor.position;
                if (cursor.position < cursor.input.size() &&
                    (cursor.input[cursor.position] == L'-' || cursor.input[cursor.position] == L'+'))
                {
                    if (cursor.input[cursor.position] == L'-')
                        cursor.commands.push_back(Command::CommandSIGN);
                    ++cursor.position;
                }
                size_t exponentDigits = 0;
                while (cursor.position < cursor.input.size() && cursor.input[cursor.position] >= L'0' &&
                       cursor.input[cursor.position] <= L'9')
                {
                    if (++exponentDigits > 4)
                        return false;
                    cursor.commands.push_back(
                        static_cast<Command>(static_cast<int>(Command::Command0) + cursor.input[cursor.position++] - L'0'));
                }
                if (exponentDigits == 0)
                    return false;
            }
            return true;
        }

        bool ParsePasteExpression(PasteCursor& cursor, unsigned depth);

        bool ParsePasteOperand(PasteCursor& cursor, unsigned depth)
        {
            SkipPasteSpace(cursor);
            if (depth > 32 || cursor.position == cursor.input.size())
                return false;
            bool negative = false;
            if (cursor.input[cursor.position] == L'+' || cursor.input[cursor.position] == L'-')
            {
                negative = cursor.input[cursor.position++] == L'-';
                SkipPasteSpace(cursor);
            }
            if (cursor.position == cursor.input.size())
                return false;
            if (cursor.input[cursor.position] != L'(')
                return ParsePasteNumber(cursor, negative);
            if (cursor.mode != Mode::Scientific && cursor.mode != Mode::Programmer)
                return false;
            ++cursor.position;
            cursor.commands.push_back(Command::CommandOPENP);
            if (!ParsePasteExpression(cursor, depth + 1))
                return false;
            SkipPasteSpace(cursor);
            if (cursor.position == cursor.input.size() || cursor.input[cursor.position++] != L')')
                return false;
            cursor.commands.push_back(Command::CommandCLOSEP);
            if (negative)
                cursor.commands.push_back(Command::CommandSIGN);
            return true;
        }

        bool ParsePasteExpression(PasteCursor& cursor, unsigned depth)
        {
            if (!ParsePasteOperand(cursor, depth))
                return false;
            while (true)
            {
                SkipPasteSpace(cursor);
                if (cursor.position == cursor.input.size() || cursor.input[cursor.position] == L')' ||
                    cursor.input[cursor.position] == L'=')
                    return true;
                if (cursor.mode == Mode::Converter)
                    return false;
                Command operation;
                switch (cursor.input[cursor.position])
                {
                case L'+':
                    operation = Command::CommandADD;
                    break;
                case L'-':
                    operation = Command::CommandSUB;
                    break;
                case L'*':
                    operation = Command::CommandMUL;
                    break;
                case L'/':
                    operation = Command::CommandDIV;
                    break;
                case L'^':
                    if (cursor.mode != Mode::Scientific)
                        return false;
                    operation = Command::CommandPWR;
                    break;
                case L'%':
                    if (cursor.mode != Mode::Scientific && cursor.mode != Mode::Programmer)
                        return false;
                    operation = Command::CommandMOD;
                    break;
                default:
                    return false;
                }
                ++cursor.position;
                cursor.commands.push_back(operation);
                if (!ParsePasteOperand(cursor, depth))
                    return false;
            }
        }

        std::optional<std::vector<Command>> ParsePaste(std::wstring_view input, Mode mode, uint32_t radix, uint32_t width, wchar_t decimal)
        {
            if (input.empty() || input.size() > 512 || mode == Mode::Date)
                return std::nullopt;
            PasteCursor cursor{input, mode, radix, width, decimal};
            if (!ParsePasteExpression(cursor, 0))
                return std::nullopt;
            SkipPasteSpace(cursor);
            if (cursor.position < input.size() && input[cursor.position] == L'=' && mode != Mode::Converter)
            {
                cursor.commands.push_back(Command::CommandEQU);
                ++cursor.position;
                SkipPasteSpace(cursor);
            }
            if (cursor.position != input.size())
                return std::nullopt;
            return cursor.commands;
        }

        std::optional<std::vector<Command>> BuildHistoryCommands(
            const std::shared_ptr<std::vector<std::shared_ptr<IExpressionCommand>>>& expression)
        {
            if (!expression || expression->empty())
                return std::nullopt;
            std::vector<Command> result;
            for (const auto& item : *expression)
            {
                if (!item || result.size() > 8192)
                    return std::nullopt;
                switch (item->GetCommandType())
                {
                case CalculationManager::CommandType::UnaryCommand: {
                    const auto command = std::dynamic_pointer_cast<IUnaryCommand>(item);
                    if (!command || !command->GetCommands())
                        return std::nullopt;
                    for (int code : *command->GetCommands())
                        result.push_back(static_cast<Command>(code));
                    break;
                }
                case CalculationManager::CommandType::BinaryCommand: {
                    const auto command = std::dynamic_pointer_cast<IBinaryCommand>(item);
                    if (!command)
                        return std::nullopt;
                    result.push_back(static_cast<Command>(command->GetCommand()));
                    break;
                }
                case CalculationManager::CommandType::Parentheses: {
                    const auto command = std::dynamic_pointer_cast<IParenthesisCommand>(item);
                    if (!command)
                        return std::nullopt;
                    result.push_back(static_cast<Command>(command->GetCommand()));
                    break;
                }
                case CalculationManager::CommandType::OperandCommand: {
                    const auto command = std::dynamic_pointer_cast<IOpndCommand>(item);
                    if (!command || !command->GetCommands())
                        return std::nullopt;
                    bool negative = command->IsNegative();
                    for (int code : *command->GetCommands())
                    {
                        result.push_back(static_cast<Command>(code));
                        if (negative && code != static_cast<int>(Command::Command0))
                        {
                            result.push_back(Command::CommandSIGN);
                            negative = false;
                        }
                    }
                    break;
                }
                }
            }
            if (result.empty() || result.size() > 8192)
                return std::nullopt;
            return result;
        }

        Button MakeButton(std::wstring title, Command command, std::wstring name = L"")
        {
            return {title, command, name.empty() ? title : name};
        }
    } // namespace

    std::vector<std::vector<Button>> BuildButtonRows(Mode mode, wchar_t decimal, bool scientificShift)
    {
        const auto point = MakeButton(std::wstring(1, decimal), Command::CommandPNT, L"Decimal point");
        const auto back = MakeButton(L"⌫", Command::CommandBACK, L"Backspace");
        const auto divide = MakeButton(L"÷", Command::CommandDIV, L"Divide");
        const auto multiply = MakeButton(L"×", Command::CommandMUL, L"Multiply");
        const auto subtract = MakeButton(L"−", Command::CommandSUB, L"Subtract");
        const auto add = MakeButton(L"+", Command::CommandADD, L"Add");
        const auto equals = MakeButton(L"=", Command::CommandEQU, L"Equals");
        const auto sign = MakeButton(L"±", Command::CommandSIGN, L"Change sign");
        const auto clear = MakeButton(L"C", Command::CommandCLEAR, L"Clear");
        const auto entry = MakeButton(L"CE", Command::CommandCENTR, L"Clear entry");
        const auto digit = [](int value) {
            return MakeButton(std::wstring(1, L'0' + value), static_cast<Command>(static_cast<int>(Command::Command0) + value));
        };
        if (mode == Mode::Converter)
        {
            return {{clear, sign, back},
                    {digit(7), digit(8), digit(9)},
                    {digit(4), digit(5), digit(6)},
                    {digit(1), digit(2), digit(3)},
                    {digit(0), point, entry}};
        }
        if (mode == Mode::Scientific)
        {
            return {
                {MakeButton(L"2nd", Command::CommandINV, L"Second functions"), MakeButton(L"π", Command::CommandPI, L"Pi"),
                 MakeButton(L"e", Command::CommandEuler, L"Euler's number"), clear, back},
                {scientificShift ? MakeButton(L"x³", Command::CommandCUB, L"Cube") : MakeButton(L"x²", Command::CommandSQR, L"Square"),
                 MakeButton(L"1/x", Command::CommandREC, L"Reciprocal"), MakeButton(L"|x|", Command::CommandAbs, L"Absolute value"),
                 MakeButton(L"exp", Command::CommandEXP, L"Exponent"), MakeButton(L"mod", Command::CommandMOD)},
                {scientificShift ? MakeButton(L"∛x", Command::CommandCUBEROOT, L"Cube root")
                                 : MakeButton(L"√x", Command::CommandSQRT, L"Square root"),
                 MakeButton(L"(", Command::CommandOPENP), MakeButton(L")", Command::CommandCLOSEP),
                 MakeButton(L"n!", Command::CommandFAC, L"Factorial"), divide},
                {scientificShift ? MakeButton(L"ʸ√x", Command::CommandROOT, L"Root") : MakeButton(L"xʸ", Command::CommandPWR, L"Power"),
                 digit(7), digit(8), digit(9), multiply},
                {scientificShift ? MakeButton(L"2ˣ", Command::CommandPOW2, L"Two to the power of x")
                                 : MakeButton(L"10ˣ", Command::CommandPOW10, L"Ten to the power of x"),
                 digit(4), digit(5), digit(6), subtract},
                {scientificShift ? MakeButton(L"logᵧx", Command::CommandLogBaseY, L"Logarithm with base y")
                                 : MakeButton(L"log", Command::CommandLOG),
                 digit(1), digit(2), digit(3), add},
                {scientificShift ? MakeButton(L"eˣ", Command::CommandPOWE, L"e to the power of x") : MakeButton(L"ln", Command::CommandLN),
                 sign, digit(0), point, equals}};
        }
        if (mode == Mode::Programmer)
        {
            return {{MakeButton(L"A", Command::CommandA), MakeButton(L"B", Command::CommandB), MakeButton(L"C", Command::CommandC),
                     MakeButton(L"D", Command::CommandD), MakeButton(L"E", Command::CommandE), MakeButton(L"F", Command::CommandF)},
                    {MakeButton(L"AND", Command::CommandAnd), MakeButton(L"OR", Command::CommandOR),
                     MakeButton(L"XOR", Command::CommandXor), MakeButton(L"NOT", Command::CommandNot),
                     MakeButton(L"<<", Command::CommandLSHF), MakeButton(L">>", Command::CommandRSHF)},
                    {MakeButton(L"(", Command::CommandOPENP), MakeButton(L")", Command::CommandCLOSEP),
                     MakeButton(L"ROL", Command::CommandROL), MakeButton(L"ROR", Command::CommandROR), entry, clear},
                    {MakeButton(L"mod", Command::CommandMOD), digit(7), digit(8), digit(9), divide, back},
                    {MakeButton(L"NAND", Command::CommandNand), digit(4), digit(5), digit(6), multiply,
                     MakeButton(L"NOR", Command::CommandNor)},
                    {sign, digit(1), digit(2), digit(3), subtract, MakeButton(L">>>", Command::CommandRSHFL)},
                    {MakeButton(L"RoLC", Command::CommandROLC), digit(0), MakeButton(L"RoRC", Command::CommandRORC), point, add, equals}};
        }
        return {{MakeButton(L"%", Command::CommandPERCENT, L"Percent"), entry, clear, back},
                {MakeButton(L"1/x", Command::CommandREC, L"Reciprocal"), MakeButton(L"x²", Command::CommandSQR, L"Square"),
                 MakeButton(L"√x", Command::CommandSQRT, L"Square root"), divide},
                {digit(7), digit(8), digit(9), multiply},
                {digit(4), digit(5), digit(6), subtract},
                {digit(1), digit(2), digit(3), add},
                {sign, digit(0), point, equals}};
    }

    std::vector<Button> BuildMemoryButtons()
    {
        return {MakeButton(L"MC", Command::CommandMCLEAR, L"Clear memory"), MakeButton(L"MR", Command::CommandRECALL, L"Recall memory"),
                MakeButton(L"M+", Command::CommandMPLUS, L"Add to memory"),
                MakeButton(L"M−", Command::CommandMMINUS, L"Subtract from memory"),
                MakeButton(L"MS", Command::CommandSTORE, L"Store in memory")};
    }

    std::vector<MenuGroup> BuildScientificMenus()
    {
        return {
            {L"Trigonometry",
             {MakeButton(L"sin", Command::CommandSIN), MakeButton(L"cos", Command::CommandCOS), MakeButton(L"tan", Command::CommandTAN),
              MakeButton(L"sec", Command::CommandSEC), MakeButton(L"csc", Command::CommandCSC), MakeButton(L"cot", Command::CommandCOT)}},
            {L"Inverse trigonometry",
             {MakeButton(L"asin", Command::CommandASIN), MakeButton(L"acos", Command::CommandACOS),
              MakeButton(L"atan", Command::CommandATAN), MakeButton(L"asec", Command::CommandASEC),
              MakeButton(L"acsc", Command::CommandACSC), MakeButton(L"acot", Command::CommandACOT)}},
            {L"Hyperbolic functions",
             {MakeButton(L"sinh", Command::CommandSINH), MakeButton(L"cosh", Command::CommandCOSH),
              MakeButton(L"tanh", Command::CommandTANH), MakeButton(L"sech", Command::CommandSECH),
              MakeButton(L"csch", Command::CommandCSCH), MakeButton(L"coth", Command::CommandCOTH)}},
            {L"Inverse hyperbolic functions",
             {MakeButton(L"asinh", Command::CommandASINH), MakeButton(L"acosh", Command::CommandACOSH),
              MakeButton(L"atanh", Command::CommandATANH), MakeButton(L"asech", Command::CommandASECH),
              MakeButton(L"acsch", Command::CommandACSCH), MakeButton(L"acoth", Command::CommandACOTH)}},
            {L"Functions",
             {MakeButton(L"|x|", Command::CommandAbs, L"Absolute value"), MakeButton(L"floor", Command::CommandFloor),
              MakeButton(L"ceil", Command::CommandCeil), MakeButton(L"rand", Command::CommandRand, L"Random number"),
              MakeButton(L"dms", Command::CommandDMS, L"Degrees, minutes, seconds"),
              MakeButton(L"deg", Command::CommandDegrees, L"Decimal degrees")}}};
    }

    std::optional<Command> FindKeyCommand(wchar_t key, wchar_t decimal)
    {
        if (key >= L'0' && key <= L'9')
        {
            return static_cast<Command>(static_cast<int>(Command::Command0) + key - L'0');
        }
        if (key == decimal || key == L'.')
        {
            return Command::CommandPNT;
        }
        switch (key)
        {
        case L'+':
            return Command::CommandADD;
        case L'-':
            return Command::CommandSUB;
        case L'*':
            return Command::CommandMUL;
        case L'/':
            return Command::CommandDIV;
        case L'%':
            return Command::CommandPERCENT;
        case L'=':
        case L'\r':
        case L'\n':
            return Command::CommandEQU;
        case L'\b':
        case 127:
            return Command::CommandBACK;
        case 27:
            return Command::CommandCLEAR;
        case L'(':
            return Command::CommandOPENP;
        case L')':
            return Command::CommandCLOSEP;
        default:
            return std::nullopt;
        }
    }

    DesktopModel::DesktopModel(std::string locale, NumberFormat format, std::string region)
        : m_resources(std::move(locale), std::move(format)),
          m_manager(std::make_unique<CalculationManager::CalculatorManager>(this, &m_resources)),
          m_unitLoader(std::make_shared<UnitDataLoader>(m_resources, region)), m_currencyLoader(std::make_shared<CurrencyDataLoader>()),
          m_converter(std::make_shared<UnitConversionManager::UnitConverter>(m_unitLoader, m_currencyLoader))
    {
        m_converter->Initialize();
        m_converter->SetViewModelCallback(std::make_shared<ConverterDisplay>(m_display, GetNumberFormat().decimal.front()));
        m_categories = m_converter->GetCategories();
        SetCategory(0);
        SetMode(Mode::Standard);
    }

    DesktopModel::~DesktopModel() = default;

    void DesktopModel::RestoreSessionContext()
    {
        m_manager->RestoreSharedContext();
    }

    void DesktopModel::SetMode(Mode mode)
    {
        m_mode = mode;
        m_scientificShift = false;
        if (mode == Mode::Date)
        {
            return;
        }
        m_radix = 10;
        m_display.notice.clear();
        m_display.expression.clear();
        m_display.is_error = false;
        switch (mode)
        {
        case Mode::Standard:
            m_manager->SetStandardMode();
            break;
        case Mode::Scientific:
            m_manager->SetScientificMode();
            break;
        case Mode::Programmer:
            m_manager->SetProgrammerMode();
            break;
        case Mode::Converter:
            m_converter->Calculate();
            UpdateCurrencyAvailability();
            break;
        case Mode::Date:
            break;
        }
        if (mode != Mode::Converter)
        {
            m_manager->SetMemorizedNumbersString();
        }
        UpdateHistory();
    }

    Mode DesktopModel::GetMode() const
    {
        return m_mode;
    }
    const DisplayState& DesktopModel::GetDisplay() const
    {
        return m_display;
    }
    const NumberFormat& DesktopModel::GetNumberFormat() const
    {
        return m_resources.GetNumberFormat();
    }

    bool DesktopModel::IsCommandEnabled(Command command) const
    {
        if (m_mode == Mode::Converter && IsCurrencySelected() && !m_currencyLoader->GetState().available)
            return false;
        if ((command == Command::CommandINV || command == Command::CommandFE) && m_mode != Mode::Scientific)
        {
            return false;
        }

        if (command == Command::CommandFE && m_display.is_error)
        {
            return false;
        }
        if (m_mode == Mode::Date)
        {
            return false;
        }
        const int bit = static_cast<int>(command) - static_cast<int>(Command::CommandBINPOS0);
        if (bit >= 0 && bit < 64)
        {
            return m_mode == Mode::Programmer && static_cast<uint32_t>(bit) < m_wordWidth;
        }
        const int digit = static_cast<int>(command) - static_cast<int>(Command::Command0);
        if (digit >= 0 && digit < 16)
        {
            return static_cast<uint32_t>(digit) < m_radix;
        }
        if (command == Command::CommandPNT && m_mode == Mode::Programmer)
        {
            return false;
        }
        if (command == Command::CommandMCLEAR || command == Command::CommandRECALL)
        {
            return !m_display.memory.empty();
        }
        return true;
    }

    void DesktopModel::SendCommand(Command command)
    {
        m_display.notice.clear();
        if (!IsCommandEnabled(command))
        {
            return;
        }
        if (m_mode == Mode::Scientific)
        {
            // The original 2nd control switches visible commands without changing the engine's inverse state.
            if (command == Command::CommandINV)
            {
                m_scientificShift = !m_scientificShift;
                return;
            }
            if (command == Command::CommandFE)
            {
                m_scientificFormat = !m_scientificFormat;
            }
            if (command == Command::CommandCUB || command == Command::CommandCUBEROOT || command == Command::CommandROOT ||
                command == Command::CommandPOW2 || command == Command::CommandLogBaseY || command == Command::CommandPOWE)
            {
                m_scientificShift = false;
            }
        }
        if (m_mode == Mode::Converter)
        {
            SendConverterCommand(command);
            return;
        }
        if (m_mode == Mode::Programmer && m_display.is_error &&
            (command == Command::CommandHex || command == Command::CommandDec || command == Command::CommandOct ||
             command == Command::CommandBin))
            m_manager->SendCommand(Command::CommandCLEAR);
        switch (command)
        {
        case Command::CommandSTORE:
            m_manager->MemorizeNumber();
            break;
        case Command::CommandMCLEAR:
            m_manager->MemorizedNumberClearAll();
            break;
        case Command::CommandRECALL:
            m_manager->MemorizedNumberLoad(0);
            break;
        case Command::CommandMPLUS:
            m_manager->MemorizedNumberAdd(0);
            break;
        case Command::CommandMMINUS:
            m_manager->MemorizedNumberSubtract(0);
            break;
        case Command::CommandHex:
            m_radix = 16;
            m_manager->SetRadix(RadixType::Hex);
            break;
        case Command::CommandDec:
            m_radix = 10;
            m_manager->SetRadix(RadixType::Decimal);
            break;
        case Command::CommandOct:
            m_radix = 8;
            m_manager->SetRadix(RadixType::Octal);
            break;
        case Command::CommandBin:
            m_radix = 2;
            m_manager->SetRadix(RadixType::Binary);
            break;
        case Command::CommandQword:
        case Command::CommandDword:
        case Command::CommandWord:
        case Command::CommandByte:
            if (m_mode == Mode::Programmer)
            {
                m_wordWidth = 64u >> (static_cast<int>(command) - static_cast<int>(Command::CommandQword));
            }
            m_manager->SendCommand(command);
            break;
        default:
            m_manager->SendCommand(command);
            break;
        }
        UpdateHistory();
    }

    void DesktopModel::SendConverterCommand(Command command)
    {
        using ConverterCommand = UnitConversionManager::Command;
        const int digit = static_cast<int>(command) - static_cast<int>(Command::Command0);
        if (digit >= 0 && digit <= 9)
        {
            m_converter->SendCommand(static_cast<ConverterCommand>(digit));
            return;
        }
        switch (command)
        {
        case Command::CommandPNT:
            m_converter->SendCommand(ConverterCommand::Decimal);
            break;
        case Command::CommandSIGN:
            m_converter->SendCommand(ConverterCommand::Negate);
            break;
        case Command::CommandBACK:
            m_converter->SendCommand(ConverterCommand::Backspace);
            break;
        case Command::CommandCLEAR:
        case Command::CommandCENTR:
            m_converter->SendCommand(ConverterCommand::Clear);
            break;
        default:
            break;
        }
    }

    bool DesktopModel::PressKey(wchar_t key)
    {
        if (m_mode == Mode::Converter && IsCurrencySelected() && !m_currencyLoader->GetState().available)
            return false;
        if (m_mode == Mode::Programmer && std::towupper(key) >= L'A' && std::towupper(key) <= L'F')
        {
            SendCommand(static_cast<Command>(static_cast<int>(Command::CommandA) + std::towupper(key) - L'A'));
            return true;
        }
        std::optional<Command> shortcut;
        if (m_mode == Mode::Scientific)
        {
            switch (std::towupper(key))
            {
            case L'^':
            case L'Y':
                shortcut = Command::CommandPWR;
                break;
            case L'%':
                shortcut = Command::CommandMOD;
                break;
            case L'!':
                shortcut = Command::CommandFAC;
                break;
            case L'@':
                shortcut = Command::CommandSQRT;
                break;
            case L'#':
                shortcut = Command::CommandCUB;
                break;
            case L'[':
                shortcut = Command::CommandFloor;
                break;
            case L']':
                shortcut = Command::CommandCeil;
                break;
            case L'|':
                shortcut = Command::CommandAbs;
                break;
            case L'S':
                shortcut = Command::CommandSIN;
                break;
            case L'O':
                shortcut = Command::CommandCOS;
                break;
            case L'T':
                shortcut = Command::CommandTAN;
                break;
            case L'U':
                shortcut = Command::CommandSEC;
                break;
            case L'I':
                shortcut = Command::CommandCSC;
                break;
            case L'J':
                shortcut = Command::CommandCOT;
                break;
            case L'X':
                shortcut = Command::CommandEXP;
                break;
            case L'M':
                shortcut = Command::CommandDMS;
                break;
            case L'V':
                shortcut = Command::CommandFE;
                break;
            case L'R':
                shortcut = Command::CommandREC;
                break;
            case L'L':
                shortcut = Command::CommandLOG;
                break;
            case L'N':
                shortcut = Command::CommandLN;
                break;
            case L'P':
                shortcut = Command::CommandPI;
                break;
            case L'Q':
                shortcut = Command::CommandSQR;
                break;
            case L'G':
                shortcut = Command::CommandPOW2;
                break;
            case L'B':
                shortcut = Command::CommandCUBEROOT;
                break;
            }
        }
        else if (m_mode == Mode::Programmer)
        {
            switch (key)
            {
            case L'%':
                shortcut = Command::CommandMOD;
                break;
            case L'|':
                shortcut = Command::CommandOR;
                break;
            case L'~':
                shortcut = Command::CommandNot;
                break;
            case L'&':
                shortcut = Command::CommandAnd;
                break;
            case L'^':
                shortcut = Command::CommandXor;
                break;
            case L'.':
                shortcut = Command::CommandNand;
                break;
            case L'\\':
                shortcut = Command::CommandNor;
                break;
            case L'<':
                shortcut = Command::CommandLSHF;
                break;
            case L'>':
                shortcut = Command::CommandRSHF;
                break;
            }
        }
        else if (m_mode == Mode::Converter && key == L'-')
            shortcut = Command::CommandSIGN;
        if (shortcut)
        {
            SendCommand(*shortcut);
            return true;
        }
        const auto command = FindKeyCommand(key, m_resources.GetNumberFormat().decimal.front());
        if (!command)
        {
            return false;
        }
        SendCommand(*command);
        return true;
    }

    bool DesktopModel::PasteNumber(std::wstring_view number)
    {
        if (number.size() > 512)
        {
            m_display.notice = L"Paste at most 512 characters";
            return false;
        }
        std::wstring value(number);
        const auto& format = GetNumberFormat();
        if (!format.thousands.empty() && format.thousands.front() != format.decimal.front())
            value.erase(std::remove(value.begin(), value.end(), format.thousands.front()), value.end());
        const auto commands = ParsePaste(value, m_mode, m_radix, m_wordWidth, format.decimal.front());
        if (!commands)
        {
            m_display.notice = L"Paste a valid number or expression in the selected base and locale";
            return false;
        }
        SendCommand(Command::CommandCENTR);
        for (const auto command : *commands)
            SendCommand(command);
        return true;
    }

    void DesktopModel::RecallMemory(size_t index)
    {
        if (index < m_display.memory.size())
        {
            m_manager->MemorizedNumberLoad(static_cast<unsigned int>(index));
        }
    }

    void DesktopModel::AddToMemory(size_t index)
    {
        if (index < m_display.memory.size())
        {
            m_manager->MemorizedNumberAdd(static_cast<unsigned int>(index));
        }
    }

    void DesktopModel::SubtractFromMemory(size_t index)
    {
        if (index < m_display.memory.size())
        {
            m_manager->MemorizedNumberSubtract(static_cast<unsigned int>(index));
        }
    }

    void DesktopModel::RemoveMemory(size_t index)
    {
        if (index < m_display.memory.size())
        {
            m_manager->MemorizedNumberClear(static_cast<unsigned int>(index));
            m_manager->SetMemorizedNumbersString();
        }
    }

    void DesktopModel::RecallHistory(size_t index)
    {
        if ((m_mode != Mode::Standard && m_mode != Mode::Scientific) || index >= m_display.history.size())
            return;
        const auto item = m_manager->GetHistoryItem(static_cast<unsigned int>(index));
        if (!item)
            return;
        const auto history = item->historyItemVector;
        const auto commands = BuildHistoryCommands(history.spCommands);
        if (!commands)
        {
            m_display.notice = L"This history item cannot be restored";
            return;
        }
        // History omits Equals. Replay the stored expression, preserving its pending operation and avoiding a duplicate history entry.
        m_manager->SetInHistoryItemLoadMode(true);
        try
        {
            if (m_mode == Mode::Scientific)
                m_manager->SetScientificMode();
            else
                m_manager->SetStandardMode();
            for (const auto command : *commands)
                m_manager->SendCommand(command);
            m_manager->SendCommand(Command::CommandFE);
            m_manager->SendCommand(Command::CommandFE);
        }
        catch (...)
        {
            m_manager->SetInHistoryItemLoadMode(false);
            throw;
        }
        m_manager->SetInHistoryItemLoadMode(false);
        if (!m_display.is_error)
        {
            SetExpressionDisplay(history.spTokens, history.spCommands);
            SetPrimaryDisplay(history.result, false);
            m_display.notice.clear();
        }
        UpdateHistory();
    }

    void DesktopModel::RemoveHistory(size_t index)
    {
        if (index < m_display.history.size())
        {
            m_manager->RemoveHistoryItem(static_cast<unsigned int>(index));
            UpdateHistory();
        }
    }

    void DesktopModel::ClearHistory()
    {
        m_manager->ClearHistory();
        UpdateHistory();
    }
    std::wstring DesktopModel::GetRadixDisplay(uint32_t radix)
    {
        return m_manager->GetResultForRadix(radix, 64, true);
    }

    uint32_t DesktopModel::GetRadix() const
    {
        return m_radix;
    }

    uint32_t DesktopModel::GetWordWidth() const
    {
        return m_wordWidth;
    }

    Command DesktopModel::GetAngleCommand() const
    {
        return m_manager->GetCurrentDegreeMode();
    }

    bool DesktopModel::GetScientificShift() const
    {
        return m_scientificShift;
    }

    bool DesktopModel::GetScientificFormat() const
    {
        return m_scientificFormat;
    }

    bool DesktopModel::IsBitSet(unsigned int bit) const
    {
        if (m_mode != Mode::Programmer || bit >= m_wordWidth)
        {
            return false;
        }
        const auto binary = m_manager->GetResultForRadix(2, 64, false);
        return bit < binary.size() && binary[binary.size() - 1 - bit] == L'1';
    }

    const std::vector<UnitConversionManager::Category>& DesktopModel::GetCategories() const
    {
        return m_categories;
    }
    const std::vector<UnitConversionManager::Unit>& DesktopModel::GetUnits() const
    {
        return m_units;
    }
    size_t DesktopModel::GetCategoryIndex() const
    {
        return m_categoryIndex;
    }
    size_t DesktopModel::GetFromUnitIndex() const
    {
        return m_fromIndex;
    }
    size_t DesktopModel::GetToUnitIndex() const
    {
        return m_toIndex;
    }

    void DesktopModel::SetCategory(size_t index)
    {
        if (index >= m_categories.size())
        {
            return;
        }
        m_categoryIndex = index;
        if (m_mode == Mode::Converter)
        {
            m_display.notice.clear();
            m_display.is_error = false;
        }
        const auto selection = m_converter->SetCurrentCategory(m_categories[index]);
        m_units.clear();
        for (const auto& unit : std::get<0>(selection))
        {
            if (!unit.isWhimsical)
            {
                m_units.push_back(unit);
            }
        }
        m_fromIndex = 0;
        m_toIndex = m_units.size() > 1 ? 1 : 0;
        for (size_t i = 0; i < m_units.size(); ++i)
        {
            if (m_units[i] == std::get<1>(selection))
            {
                m_fromIndex = i;
            }
            if (m_units[i] == std::get<2>(selection))
            {
                m_toIndex = i;
            }
        }
        SetUnits(m_fromIndex, m_toIndex);
        UpdateCurrencyAvailability();
    }

    void DesktopModel::SetUnits(size_t from, size_t to)
    {
        if (from >= m_units.size() || to >= m_units.size())
        {
            return;
        }
        m_fromIndex = from;
        m_toIndex = to;
        m_converter->SetCurrentUnitTypes(m_units[from], m_units[to]);
    }

    bool DesktopModel::IsCurrencySelected() const
    {
        return m_categories[m_categoryIndex].id == CURRENCY_CATEGORY_ID;
    }

    const CurrencyState& DesktopModel::GetCurrencyState() const
    {
        return m_currencyLoader->GetState();
    }

    double DesktopModel::GetCurrencyRatio() const
    {
        if (!IsCurrencySelected() || !GetCurrencyState().available || m_fromIndex >= m_units.size() || m_toIndex >= m_units.size())
            return 0;
        const auto ratios = m_currencyLoader->LoadOrderedRatios(m_units[m_fromIndex]);
        const auto found = ratios.find(m_units[m_toIndex]);
        return found == ratios.end() ? 0 : found->second.ratio;
    }

    void DesktopModel::UpdateCurrencyAvailability()
    {
        if (m_mode != Mode::Converter || !IsCurrencySelected())
            return;
        const auto& state = GetCurrencyState();
        m_display.is_error = !state.available;
        m_display.notice = state.error.empty() ? std::wstring{} : BridgeJson::DecodeUtf8(state.error);
        if (!state.available)
        {
            // UnitConverter echoes input for empty units; unavailable rates must not show a conversion.
            m_display.converted.clear();
            if (m_display.notice.empty())
                m_display.notice = L"Currency rates unavailable. Connect to update rates.";
        }
    }

    void DesktopModel::SetCurrencyRates(std::string_view referenceDate, std::string_view fetchedAt, std::string_view rates, bool fromCache,
                                        int provider, std::string_view referenceEndDate)
    {
        const auto display = m_display;
        const auto category = m_categoryIndex;
        const int fromId = m_units.empty() ? -1 : m_units[m_fromIndex].id;
        const int toId = m_units.empty() ? -1 : m_units[m_toIndex].id;
        m_currencyLoader->SetRates(referenceDate, fetchedAt, rates, fromCache, provider, referenceEndDate);
        m_converter->ResetCategoriesAndRatios();
        SetCategory(category);
        auto from = m_fromIndex;
        auto to = m_toIndex;
        for (size_t index = 0; index < m_units.size(); ++index)
        {
            if (m_units[index].id == fromId)
                from = index;
            if (m_units[index].id == toId)
                to = index;
        }
        SetUnits(from, to);
        if (m_mode != Mode::Converter)
            m_display = display;
    }

    void DesktopModel::SetCurrencyProvider(int provider)
    {
        m_currencyLoader->SetProvider(provider);
    }

    void DesktopModel::SetCurrencyStatus(int status, std::string message)
    {
        BridgeJson::DecodeUtf8(message);
        m_currencyLoader->SetStatus(status, std::move(message));
        UpdateCurrencyAvailability();
    }

    void DesktopModel::SetPrimaryDisplay(const std::wstring& value, bool isError)
    {
        m_display.primary = value;
        m_display.is_error = isError;
    }
    void DesktopModel::SetIsInError(bool isError)
    {
        m_display.is_error = isError;
    }
    void DesktopModel::SetExpressionDisplay(const std::shared_ptr<std::vector<std::pair<std::wstring, int>>>& tokens,
                                            const std::shared_ptr<std::vector<std::shared_ptr<IExpressionCommand>>>&)
    {
        m_display.expression.clear();
        if (!tokens)
        {
            return;
        }
        for (const auto& token : *tokens)
        {
            if (!m_display.expression.empty())
            {
                m_display.expression += L' ';
            }
            m_display.expression += token.first;
        }
    }
    void DesktopModel::SetParenthesisNumber(unsigned int)
    {
    }
    void DesktopModel::OnNoRightParenAdded()
    {
        m_display.notice = L"Enter an opening parenthesis first";
    }
    void DesktopModel::MaxDigitsReached()
    {
        m_display.notice = L"Maximum digits reached";
    }
    void DesktopModel::BinaryOperatorReceived()
    {
    }
    void DesktopModel::OnHistoryItemAdded(unsigned int)
    {
    }
    void DesktopModel::SetMemorizedNumbers(const std::vector<std::wstring>& values)
    {
        m_display.memory = values;
    }
    void DesktopModel::MemoryItemChanged(unsigned int)
    {
    }
    void DesktopModel::InputChanged()
    {
    }

    void DesktopModel::UpdateHistory()
    {
        m_display.history.clear();
        if (m_mode == Mode::Programmer || m_mode == Mode::Converter || m_mode == Mode::Date)
        {
            return;
        }
        for (const auto& item : m_manager->GetHistoryItems())
        {
            m_display.history.push_back({item->historyItemVector.expression, item->historyItemVector.result});
        }
    }
} // namespace Desktop
