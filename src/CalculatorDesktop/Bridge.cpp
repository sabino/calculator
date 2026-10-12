// Licensed under the MIT License.
#include "Bridge.h"
#include "AboutInfo.h"
#include "BridgeJson.h"
#include "DateCalculator.h"
#include "DesktopModel.h"
#include <algorithm>
#include <iomanip>
#include <sstream>
#include <memory>
#include <mutex>
#include <set>
#include <stdexcept>
#include <unordered_map>

#ifndef CALCULATOR_HAS_GRAPHING
#define CALCULATOR_HAS_GRAPHING 0
#endif
#ifndef CALCULATOR_GRAPHING_VERSION
#define CALCULATOR_GRAPHING_VERSION ""
#endif

struct CalcSession
{
    std::unique_ptr<Desktop::DesktopModel> model;
    std::string result;
    std::string error;
    bool error_fallback = false;
};

namespace
{
    using CalculationManager::Command;
    using Desktop::Mode;
    using Desktop::BridgeJson::DecodeUtf8;
    using Desktop::BridgeJson::EncodeUtf8;
    using Desktop::BridgeJson::EscapeJson;

    constexpr std::size_t MAX_TEXT_BYTES = 65536;
    constexpr const char* FALLBACK_ERROR = "Native calculator bridge failed";
    std::mutex sessions_mutex;
    std::unordered_map<CalcHandle, std::unique_ptr<CalcSession>> sessions;
    thread_local std::string thread_error;
    thread_local bool thread_error_fallback = false;

    void SetError(CalcSession* session, const char* message) noexcept
    {
        try
        {
            DecodeUtf8(message);
            if (session)
            {
                session->error = message;
                session->error_fallback = false;
            }
            else
            {
                thread_error = message;
                thread_error_fallback = false;
            }
        }
        catch (...)
        {
            if (session)
            {
                session->error_fallback = true;
            }
            else
            {
                thread_error_fallback = true;
            }
        }
    }

    CalcSession* FindSession(CalcHandle handle)
    {
        const auto found = sessions.find(handle);
        if (found == sessions.end())
        {
            SetError(nullptr, "Invalid or destroyed calculator session");
            return nullptr;
        }
        return found->second.get();
    }

    void BeginCall(CalcSession& session)
    {
        session.result.clear();
        session.error.clear();
        session.error_fallback = false;
    }

    template <class Action> int RunAction(CalcHandle handle, Action action) noexcept
    {
        try
        {
            const std::lock_guard<std::mutex> lock(sessions_mutex);
            auto* session = FindSession(handle);
            if (!session)
                return 0;
            BeginCall(*session);
            try
            {
                session->model->RestoreSessionContext();
                action(*session->model);
                return 1;
            }
            catch (const std::exception& error)
            {
                SetError(session, error.what());
            }
            catch (...)
            {
                SetError(session, FALLBACK_ERROR);
            }
        }
        catch (...)
        {
            SetError(nullptr, FALLBACK_ERROR);
        }
        return 0;
    }

    template <class Getter> const char* RunJson(CalcHandle handle, Getter getter) noexcept
    {
        try
        {
            const std::lock_guard<std::mutex> lock(sessions_mutex);
            auto* session = FindSession(handle);
            if (!session)
                return nullptr;
            BeginCall(*session);
            try
            {
                session->model->RestoreSessionContext();
                session->result = getter(*session->model);
                return session->result.c_str();
            }
            catch (const std::exception& error)
            {
                SetError(session, error.what());
            }
            catch (...)
            {
                SetError(session, FALLBACK_ERROR);
            }
        }
        catch (...)
        {
            SetError(nullptr, FALLBACK_ERROR);
        }
        return nullptr;
    }

    std::string ReadText(const char* text, std::size_t limit = MAX_TEXT_BYTES)
    {
        if (!text)
            throw std::invalid_argument("UTF-8 input must not be null");
        std::size_t size = 0;
        while (size <= limit && text[size] != '\0')
            ++size;
        if (size > limit)
            throw std::invalid_argument("UTF-8 input exceeds the size limit");
        std::string result(text, size);
        DecodeUtf8(result);
        return result;
    }

    std::string ReadSetting(const char* text, const char* fallback, std::size_t limit)
    {
        if (!text)
            return fallback;
        auto value = ReadText(text, limit);
        return value.empty() ? fallback : value;
    }

    std::string Quote(std::string_view text)
    {
        return "\"" + EscapeJson(text) + "\"";
    }

    std::string Quote(std::wstring_view text)
    {
        return Quote(EncodeUtf8(text));
    }

    std::vector<Desktop::Button> GetAngleButtons()
    {
        return {{L"DEG", Command::CommandDEG, L"Degrees"},
                {L"RAD", Command::CommandRAD, L"Radians"},
                {L"GRAD", Command::CommandGRAD, L"Gradians"}};
    }

    std::vector<Desktop::Button> GetBaseButtons()
    {
        return {{L"HEX", Command::CommandHex, L"Hexadecimal"},
                {L"DEC", Command::CommandDec, L"Decimal"},
                {L"OCT", Command::CommandOct, L"Octal"},
                {L"BIN", Command::CommandBin, L"Binary"}};
    }

    std::vector<Desktop::Button> GetWordButtons()
    {
        return {{L"QWORD", Command::CommandQword, L"64 bits"},
                {L"DWORD", Command::CommandDword, L"32 bits"},
                {L"WORD", Command::CommandWord, L"16 bits"},
                {L"BYTE", Command::CommandByte, L"8 bits"}};
    }

    std::set<int> GetAllowedCommands(Mode mode)
    {
        std::set<int> result;
        if (mode == Mode::Date)
            return result;
        const auto addButtons = [&](const std::vector<Desktop::Button>& buttons) {
            for (const auto& button : buttons)
                result.insert(static_cast<int>(button.command));
        };
        for (const auto& row : Desktop::BuildButtonRows(mode))
            addButtons(row);
        if (mode != Mode::Converter)
            addButtons(Desktop::BuildMemoryButtons());
        if (mode == Mode::Scientific)
        {
            addButtons(GetAngleButtons());
            for (const auto& row : Desktop::BuildButtonRows(mode, L'.', true))
                addButtons(row);
            result.insert(static_cast<int>(Command::CommandFE));
            result.insert(static_cast<int>(Command::CommandCENTR));
            result.insert(static_cast<int>(Command::CommandCHOP));
            for (const auto& group : Desktop::BuildScientificMenus())
                addButtons(group.buttons);
        }
        if (mode == Mode::Programmer)
        {
            addButtons(GetBaseButtons());
            addButtons(GetWordButtons());
            for (int bit = 0; bit < 64; ++bit)
                result.insert(static_cast<int>(Command::CommandBINPOS0) + bit);
        }
        return result;
    }

    bool IsEnabled(const Desktop::DesktopModel& model, const std::set<int>& commands, Command command)
    {
        return commands.count(static_cast<int>(command)) != 0 && model.IsCommandEnabled(command);
    }

    std::string SerializeButtons(const std::vector<Desktop::Button>& buttons, const Desktop::DesktopModel& model,
                                 const std::set<int>& commands)
    {
        std::string result = "[";
        for (const auto& button : buttons)
        {
            if (result.size() > 1)
                result += ',';
            result += "{\"title\":" + Quote(button.title) + ",\"command\":" + std::to_string(static_cast<int>(button.command)) +
                      ",\"accessibleName\":" + Quote(button.accessible_name) +
                      ",\"enabled\":" + (IsEnabled(model, commands, button.command) ? "true" : "false") + "}";
        }
        return result + "]";
    }

    std::string SerializeState(Desktop::DesktopModel& model)
    {
        const auto& display = model.GetDisplay();
        const auto commands = GetAllowedCommands(model.GetMode());
        std::string result = "{\"mode\":" + std::to_string(static_cast<int>(model.GetMode())) + ",\"primary\":" + Quote(display.primary) +
                             ",\"expression\":" + Quote(display.expression) + ",\"converted\":" + Quote(display.converted) +
                             ",\"notice\":" + Quote(display.notice) + ",\"isError\":" + (display.is_error ? "true" : "false") +
                             ",\"memory\":[";
        for (std::size_t index = 0; index < display.memory.size(); ++index)
        {
            if (index != 0)
                result += ',';
            result += Quote(display.memory[index]);
        }
        result += "],\"history\":[";
        for (std::size_t index = 0; index < display.history.size(); ++index)
        {
            if (index != 0)
                result += ',';
            const auto& item = display.history[index];
            result += "{\"expression\":" + Quote(item.expression) + ",\"result\":" + Quote(item.result) + "}";
        }
        result += "],\"buttonRows\":[";
        if (model.GetMode() != Mode::Date)
        {
            const auto rows =
                Desktop::BuildButtonRows(model.GetMode(), model.GetNumberFormat().decimal.front(), model.GetScientificShift());
            for (std::size_t index = 0; index < rows.size(); ++index)
            {
                if (index != 0)
                    result += ',';
                result += SerializeButtons(rows[index], model, commands);
            }
        }
        result += "],\"memoryButtons\":" + SerializeButtons(Desktop::BuildMemoryButtons(), model, commands) + ",\"scientificMenus\":[";
        const auto menus = Desktop::BuildScientificMenus();
        for (std::size_t index = 0; index < menus.size(); ++index)
        {
            if (index != 0)
                result += ',';
            result +=
                "{\"title\":" + Quote(menus[index].title) + ",\"buttons\":" + SerializeButtons(menus[index].buttons, model, commands) + "}";
        }
        const auto formatButton = SerializeButtons({{L"F-E", Command::CommandFE, L"Toggle scientific notation"}}, model, commands);
        result += "],\"scientificShift\":" + std::string(model.GetScientificShift() ? "true" : "false") +
                  ",\"scientificFormat\":" + (model.GetScientificFormat() ? "true" : "false") +
                  ",\"scientificFormatButton\":" + formatButton.substr(1, formatButton.size() - 2) + ",\"radixDisplays\":[";
        if (model.GetMode() == Mode::Programmer)
        {
            for (const unsigned radix : {16u, 10u, 8u, 2u})
            {
                if (radix != 16)
                    result += ',';
                result += "{\"radix\":" + std::to_string(radix) + ",\"value\":" + Quote(model.GetRadixDisplay(radix)) + "}";
            }
        }
        result += "],\"radix\":" + std::to_string(model.GetRadix()) + ",\"wordWidth\":" + std::to_string(model.GetWordWidth()) +
                  ",\"angleCommand\":" + std::to_string(static_cast<int>(model.GetAngleCommand())) +
                  ",\"angleButtons\":" + SerializeButtons(GetAngleButtons(), model, commands) +
                  ",\"baseButtons\":" + SerializeButtons(GetBaseButtons(), model, commands) +
                  ",\"wordButtons\":" + SerializeButtons(GetWordButtons(), model, commands) + ",\"bitValues\":[";
        for (unsigned position = 0; position < 64; ++position)
        {
            if (position != 0)
                result += ',';
            result += model.IsBitSet(63 - position) ? "true" : "false";
        }
        result += "],\"bitButtons\":";
        std::vector<Desktop::Button> bitButtons;
        bitButtons.reserve(64);
        for (unsigned position = 0; position < 64; ++position)
        {
            const unsigned bit = 63 - position;
            bitButtons.push_back({model.IsBitSet(bit) ? L"1" : L"0", static_cast<Command>(static_cast<int>(Command::CommandBINPOS0) + bit),
                                  L"Toggle bit " + std::to_wstring(bit)});
        }
        result += SerializeButtons(bitButtons, model, commands) + ",\"categories\":[";
        const auto& categories = model.GetCategories();
        for (std::size_t index = 0; index < categories.size(); ++index)
        {
            if (index != 0)
                result += ',';
            result += Quote(categories[index].name);
        }
        result += "],\"units\":[";
        const auto& units = model.GetUnits();
        for (std::size_t index = 0; index < units.size(); ++index)
        {
            if (index != 0)
                result += ',';
            result += Quote(units[index].name);
        }
        const auto& currency = model.GetCurrencyState();
        std::ostringstream ratio;
        ratio.imbue(std::locale::classic());
        ratio << std::setprecision(17) << model.GetCurrencyRatio();
        const bool frankfurter = currency.provider_id == 1;
        return result + "],\"currency\":{\"available\":" + (currency.available ? "true" : "false") +
               ",\"providerId\":" + std::to_string(currency.provider_id) + ",\"provider\":" +
               Quote(frankfurter ? "Frankfurter" : "European Central Bank") + ",\"sourceUrl\":" +
               Quote(frankfurter ? "https://api.frankfurter.dev/v2/rates?base=EUR" : "https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml") +
               ",\"attribution\":" + Quote(frankfurter
                   ? "Rates: Frankfurter, from central banks and official sources. Cross rates calculated from blended euro reference rates."
                   : "Reference rates: European Central Bank. Cross rates calculated from euro reference rates.") +
               ",\"referenceDate\":" + Quote(currency.reference_date) + ",\"referenceEndDate\":" + Quote(currency.reference_end_date) +
               ",\"fetchedAt\":" + Quote(currency.fetched_at) +
               ",\"status\":" + Quote(currency.status) + ",\"error\":" + Quote(currency.error) + "},\"categoryIndex\":" + std::to_string(model.GetCategoryIndex()) +
               ",\"fromUnitIndex\":" + std::to_string(model.GetFromUnitIndex()) +
               ",\"toUnitIndex\":" + std::to_string(model.GetToUnitIndex()) +
               ",\"supportsNegative\":" + (categories[model.GetCategoryIndex()].supportsNegative ? "true" : "false") +
               ",\"currencyRatio\":" + ratio.str() + "}";
    }

    void RequireCalculatorMode(const Desktop::DesktopModel& model)
    {
        if (static_cast<int>(model.GetMode()) > static_cast<int>(Mode::Programmer))
            throw std::invalid_argument("Memory and history actions require a calculator mode");
    }

    void RequireIndex(int index, std::size_t size)
    {
        if (index < 0 || static_cast<std::size_t>(index) >= size)
            throw std::invalid_argument("Index is outside the available items");
    }

    Desktop::CivilDate ReadDate(int year, int month, int day)
    {
        if (month < 1 || month > 12 || day < 1 || day > 31)
            throw std::invalid_argument("Invalid civil date");
        const Desktop::CivilDate date{year, static_cast<unsigned>(month), static_cast<unsigned>(day)};
        if (!Desktop::IsValidDate(date))
            throw std::invalid_argument("Date must be valid and between 1601-01-01 and 9999-12-31");
        return date;
    }
} // namespace

extern "C"
{
    CalcHandle calc_create(const char* locale, const char* region, const char* decimal, const char* group_separator)
    {
        try
        {
            const std::lock_guard<std::mutex> lock(sessions_mutex);
            thread_error.clear();
            thread_error_fallback = false;
            Desktop::NumberFormat format;
            format.decimal = DecodeUtf8(ReadSetting(decimal, ".", 16));
            format.thousands = DecodeUtf8(group_separator ? ReadText(group_separator, 16) : "");
            if (format.decimal.size() != 1 || format.thousands.size() > 1 || format.decimal == format.thousands)
                throw std::invalid_argument("Number separators must be distinct single characters");
            const auto isReserved = [](wchar_t value) {
                return value < 0x20 || (value >= L'0' && value <= L'9') || value == L'+' || value == L'-' || value == L'*' ||
                       value == L'/' || value == L'=' || value == L'(' || value == L')';
            };
            if (isReserved(format.decimal.front()) || format.decimal.front() == L' ' ||
                (!format.thousands.empty() && isReserved(format.thousands.front())))
                throw std::invalid_argument("Invalid number separator");
            auto session = std::make_unique<CalcSession>();
            session->model =
                std::make_unique<Desktop::DesktopModel>(ReadSetting(locale, "en-US", 128), format, ReadSetting(region, "US", 32));
            const auto handle = session.get();
            sessions.emplace(handle, std::move(session));
            return handle;
        }
        catch (const std::exception& error)
        {
            SetError(nullptr, error.what());
        }
        catch (...)
        {
            SetError(nullptr, FALLBACK_ERROR);
        }
        return nullptr;
    }

    int calc_destroy(CalcHandle handle)
    {
        try
        {
            const std::lock_guard<std::mutex> lock(sessions_mutex);
            if (!FindSession(handle))
                return 0;
            sessions.erase(handle);
            return 1;
        }
        catch (...)
        {
            SetError(nullptr, FALLBACK_ERROR);
            return 0;
        }
    }

    const char* calc_last_error(CalcHandle handle)
    {
        try
        {
            const std::lock_guard<std::mutex> lock(sessions_mutex);
            if (!handle)
                return thread_error_fallback ? FALLBACK_ERROR : thread_error.c_str();
            auto* session = FindSession(handle);
            if (!session)
                return thread_error_fallback ? FALLBACK_ERROR : thread_error.c_str();
            session->result.clear();
            return session->error_fallback ? FALLBACK_ERROR : session->error.c_str();
        }
        catch (...)
        {
            return FALLBACK_ERROR;
        }
    }

    const char* calc_get_state(CalcHandle handle)
    {
        return RunJson(handle, SerializeState);
    }

    int calc_set_mode(CalcHandle handle, int mode)
    {
        return RunAction(handle, [=](Desktop::DesktopModel& model) {
            if (mode < 0 || mode > static_cast<int>(Mode::Date))
                throw std::invalid_argument("Invalid calculator mode");
            model.SetMode(static_cast<Mode>(mode));
        });
    }

    int calc_send_command(CalcHandle handle, int command)
    {
        return RunAction(handle, [=](Desktop::DesktopModel& model) {
            const auto allowed = GetAllowedCommands(model.GetMode());
            if (allowed.count(command) == 0)
                throw std::invalid_argument("Command is not available in this mode");
            const auto value = static_cast<Command>(command);
            if (!model.IsCommandEnabled(value))
                throw std::invalid_argument("Command is disabled");
            model.SendCommand(value);
        });
    }

    int calc_press_key(CalcHandle handle, const char* key)
    {
        return RunAction(handle, [=](Desktop::DesktopModel& model) {
            const auto value = DecodeUtf8(ReadText(key, 4));
            if (value.size() != 1)
                throw std::invalid_argument("Key must contain one supported Unicode scalar");
            auto command = Desktop::FindKeyCommand(value.front(), model.GetNumberFormat().decimal.front());
            if (model.GetMode() == Mode::Programmer)
            {
                const auto letter = value.front() >= L'a' && value.front() <= L'f' ? value.front() - L'a' + L'A' : value.front();
                if (letter >= L'A' && letter <= L'F')
                    command = static_cast<Command>(static_cast<int>(Command::CommandA) + letter - L'A');
            }
            if (!command || !IsEnabled(model, GetAllowedCommands(model.GetMode()), *command))
                throw std::invalid_argument("Key is not available in this mode or base");
            if (!model.PressKey(value.front()))
                throw std::invalid_argument("Key is unsupported");
        });
    }

    int calc_paste_number(CalcHandle handle, const char* number)
    {
        return RunAction(handle, [=](Desktop::DesktopModel& model) {
            if (model.GetMode() == Mode::Date)
                throw std::invalid_argument("Number input is unavailable in Date mode");
            if (model.GetMode() == Mode::Converter && model.GetCategories()[model.GetCategoryIndex()].id == Desktop::CURRENCY_CATEGORY_ID &&
                !model.GetCurrencyState().available)
                throw std::invalid_argument("Currency rates are unavailable");
            if (!model.PasteNumber(DecodeUtf8(ReadText(number))))
                throw std::invalid_argument("Invalid number for this locale and base");
        });
    }

    int calc_set_category(CalcHandle handle, int category)
    {
        return RunAction(handle, [=](Desktop::DesktopModel& model) {
            if (model.GetMode() != Mode::Converter)
                throw std::invalid_argument("Category selection requires Converter mode");
            RequireIndex(category, model.GetCategories().size());
            model.SetCategory(static_cast<std::size_t>(category));
        });
    }

    int calc_set_units(CalcHandle handle, int from, int to)
    {
        return RunAction(handle, [=](Desktop::DesktopModel& model) {
            if (model.GetMode() != Mode::Converter)
                throw std::invalid_argument("Unit selection requires Converter mode");
            RequireIndex(from, model.GetUnits().size());
            RequireIndex(to, model.GetUnits().size());
            model.SetUnits(static_cast<std::size_t>(from), static_cast<std::size_t>(to));
        });
    }

    int calc_currency_set_rates(CalcHandle handle, const char* reference_date, const char* fetched_utc, const char* rates, int from_cache)
    {
        return RunAction(handle, [=](Desktop::DesktopModel& model) {
            if (from_cache != 0 && from_cache != 1)
                throw std::invalid_argument("Currency cache flag must be zero or one");
            model.SetCurrencyRates(ReadText(reference_date, 10), ReadText(fetched_utc, 20), ReadText(rates), from_cache != 0);
        });
    }

    int calc_currency_set_provider(CalcHandle handle, int provider)
    {
        return RunAction(handle, [=](Desktop::DesktopModel& model) { model.SetCurrencyProvider(provider); });
    }

    int calc_currency_set_rates_ex(CalcHandle handle, int provider, const char* reference_date, const char* reference_end_date,
                                   const char* fetched_utc, const char* rates, int from_cache)
    {
        return RunAction(handle, [=](Desktop::DesktopModel& model) {
            if (from_cache != 0 && from_cache != 1)
                throw std::invalid_argument("Currency cache flag must be zero or one");
            model.SetCurrencyRates(ReadText(reference_date, 10), ReadText(fetched_utc, 20), ReadText(rates), from_cache != 0,
                                   provider, ReadText(reference_end_date, 10));
        });
    }

    int calc_currency_set_status(CalcHandle handle, int status, const char* message)
    {
        return RunAction(handle, [=](Desktop::DesktopModel& model) { model.SetCurrencyStatus(status, ReadText(message, 1024)); });
    }

    int calc_memory_action(CalcHandle handle, int operation, int index)
    {
        return RunAction(handle, [=](Desktop::DesktopModel& model) {
            RequireCalculatorMode(model);
            if (operation < 0 || operation > 3)
                throw std::invalid_argument("Invalid memory operation");
            RequireIndex(index, model.GetDisplay().memory.size());
            const auto selected = static_cast<std::size_t>(index);
            switch (operation)
            {
            case 0:
                model.RecallMemory(selected);
                break;
            case 1:
                model.AddToMemory(selected);
                break;
            case 2:
                model.SubtractFromMemory(selected);
                break;
            case 3:
                model.RemoveMemory(selected);
                break;
            }
        });
    }

    int calc_history_action(CalcHandle handle, int operation, int index)
    {
        return RunAction(handle, [=](Desktop::DesktopModel& model) {
            RequireCalculatorMode(model);
            if (operation == 2)
            {
                model.ClearHistory();
                return;
            }
            if (operation < 0 || operation > 1)
                throw std::invalid_argument("Invalid history operation");
            RequireIndex(index, model.GetDisplay().history.size());
            if (operation == 0)
                model.RecallHistory(static_cast<std::size_t>(index));
            else
                model.RemoveHistory(static_cast<std::size_t>(index));
        });
    }

    const char* calc_get_about(CalcHandle handle)
    {
        return RunJson(handle, [](const Desktop::DesktopModel&) {
            const auto about = Desktop::GetAboutInfo();
            return "{\"version\":" + Quote(about.version) + ",\"copyright\":" + Quote(about.copyright) +
                   ",\"license\":" + Quote(about.license) + ",\"sourceUrl\":" + Quote(about.source_url) +
                   ",\"privacyUrl\":" + Quote(about.privacy_url) + ",\"servicesUrl\":" + Quote(about.services_url) +
                   ",\"graphingEnabled\":" + (CALCULATOR_HAS_GRAPHING ? "true" : "false") +
                   ",\"graphingVersion\":" + Quote(std::string_view(CALCULATOR_GRAPHING_VERSION)) + "}";
        });
    }

    const char* calc_date_difference(CalcHandle handle, int year1, int month1, int day1, int year2, int month2, int day2)
    {
        return RunJson(handle, [=](const Desktop::DesktopModel&) {
            const auto difference = Desktop::GetDateDifference(ReadDate(year1, month1, day1), ReadDate(year2, month2, day2));
            if (!difference)
                throw std::invalid_argument("Date difference is outside the supported range");
            return "{\"years\":" + std::to_string(difference->years) + ",\"months\":" + std::to_string(difference->months) +
                   ",\"weeks\":" + std::to_string(difference->weeks) + ",\"days\":" + std::to_string(difference->days) +
                   ",\"totalDays\":" + std::to_string(difference->total_days) + "}";
        });
    }

    const char* calc_date_shift(CalcHandle handle, int year, int month, int day, int years, int months, int days, int subtract)
    {
        return RunJson(handle, [=](const Desktop::DesktopModel&) {
            if (years < 0 || months < 0 || days < 0)
                throw std::invalid_argument("Date durations must be nonnegative");
            if (subtract != 0 && subtract != 1)
                throw std::invalid_argument("Subtract flag must be 0 or 1");
            const auto start = ReadDate(year, month, day);
            const Desktop::DateDuration duration{static_cast<unsigned>(years), static_cast<unsigned>(months), static_cast<unsigned>(days)};
            const auto date = subtract ? Desktop::SubtractDateDuration(start, duration) : Desktop::AddDateDuration(start, duration);
            if (!date)
                throw std::invalid_argument("Resulting date must stay between 1601-01-01 and 9999-12-31");
            return "{\"year\":" + std::to_string(date->year) + ",\"month\":" + std::to_string(date->month) +
                   ",\"day\":" + std::to_string(date->day) + "}";
        });
    }
}
