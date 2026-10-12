// Licensed under the MIT License.
#pragma once

#include "Header Files/ICalcDisplay.h"
#include "ResourceProvider.h"
#include "UnitDataLoader.h"
#include "CurrencyDataLoader.h"
#include <optional>

namespace CalculationManager
{
    class CalculatorManager;
}

namespace Desktop
{
    enum class Mode
    {
        Standard,
        Scientific,
        Programmer,
        Converter,
        Date
    };

    struct HistoryItem
    {
        std::wstring expression;
        std::wstring result;
    };

    struct DisplayState
    {
        std::wstring primary = L"0";
        std::wstring expression;
        std::wstring converted = L"0";
        std::wstring notice;
        bool is_error = false;
        std::vector<std::wstring> memory;
        std::vector<HistoryItem> history;
    };

    struct Button
    {
        std::wstring title;
        CalculationManager::Command command;
        std::wstring accessible_name;
    };

    struct MenuGroup
    {
        std::wstring title;
        std::vector<Button> buttons;
    };

    std::vector<std::vector<Button>> BuildButtonRows(Mode mode, wchar_t decimal = L'.', bool scientificShift = false);
    std::vector<Button> BuildMemoryButtons();
    std::vector<MenuGroup> BuildScientificMenus();
    std::optional<CalculationManager::Command> FindKeyCommand(wchar_t key, wchar_t decimal = L'.');

    class DesktopModel final : public ICalcDisplay
    {
      public:
        explicit DesktopModel(std::string locale = "en-US", NumberFormat format = {}, std::string region = "US");
        ~DesktopModel();
        void RestoreSessionContext();
        void SetMode(Mode mode);
        Mode GetMode() const;
        const DisplayState& GetDisplay() const;
        const NumberFormat& GetNumberFormat() const;
        void SendCommand(CalculationManager::Command command);
        bool PressKey(wchar_t key);
        bool PasteNumber(std::wstring_view number);
        bool IsCommandEnabled(CalculationManager::Command command) const;
        void RecallMemory(size_t index);
        void AddToMemory(size_t index);
        void SubtractFromMemory(size_t index);
        void RemoveMemory(size_t index);
        void RecallHistory(size_t index);
        void RemoveHistory(size_t index);
        void ClearHistory();
        std::wstring GetRadixDisplay(uint32_t radix);
        uint32_t GetRadix() const;
        uint32_t GetWordWidth() const;
        CalculationManager::Command GetAngleCommand() const;
        bool GetScientificShift() const;
        bool GetScientificFormat() const;
        bool IsBitSet(unsigned int bit) const;

        const std::vector<UnitConversionManager::Category>& GetCategories() const;
        const std::vector<UnitConversionManager::Unit>& GetUnits() const;
        void SetCategory(size_t index);
        void SetUnits(size_t from, size_t to);
        size_t GetCategoryIndex() const;
        size_t GetFromUnitIndex() const;
        size_t GetToUnitIndex() const;
        const CurrencyState& GetCurrencyState() const;
        double GetCurrencyRatio() const;
        void SetCurrencyRates(std::string_view referenceDate, std::string_view fetchedAt, std::string_view rates, bool fromCache,
                              int provider = 0, std::string_view referenceEndDate = {});
        void SetCurrencyProvider(int provider);
        void SetCurrencyStatus(int status, std::string message);

        void SetPrimaryDisplay(const std::wstring& value, bool isError) override;
        void SetIsInError(bool isError) override;
        void SetExpressionDisplay(const std::shared_ptr<std::vector<std::pair<std::wstring, int>>>& tokens,
                                  const std::shared_ptr<std::vector<std::shared_ptr<IExpressionCommand>>>& commands) override;
        void SetParenthesisNumber(unsigned int count) override;
        void OnNoRightParenAdded() override;
        void MaxDigitsReached() override;
        void BinaryOperatorReceived() override;
        void OnHistoryItemAdded(unsigned int index) override;
        void SetMemorizedNumbers(const std::vector<std::wstring>& values) override;
        void MemoryItemChanged(unsigned int index) override;
        void InputChanged() override;

      private:
        void UpdateHistory();
        void SendConverterCommand(CalculationManager::Command command);
        bool IsCurrencySelected() const;
        void UpdateCurrencyAvailability();
        ResourceProvider m_resources;
        DisplayState m_display;
        std::unique_ptr<CalculationManager::CalculatorManager> m_manager;
        std::shared_ptr<UnitDataLoader> m_unitLoader;
        std::shared_ptr<CurrencyDataLoader> m_currencyLoader;
        std::shared_ptr<UnitConversionManager::UnitConverter> m_converter;
        Mode m_mode = Mode::Standard;
        uint32_t m_radix = 10;
        uint32_t m_wordWidth = 64;
        bool m_scientificShift = false;
        bool m_scientificFormat = false;
        std::vector<UnitConversionManager::Category> m_categories;
        std::vector<UnitConversionManager::Unit> m_units;
        size_t m_categoryIndex = 0;
        size_t m_fromIndex = 0;
        size_t m_toIndex = 0;
    };
} // namespace Desktop
