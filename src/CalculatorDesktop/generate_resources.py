"""Embed the Windows resources and unit definitions in the native desktop build."""

import ast
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def quote_wide(value: str) -> str:
    escaped = value.replace("\\", "\\\\").replace('"', '\\"')
    escaped = escaped.replace("\n", "\\n").replace("\r", "\\r").replace("\t", "\\t")
    return f'L"{escaped}"'


def parse_number(expression: str) -> float:
    node = ast.parse(expression.strip(), mode="eval").body
    if (
        isinstance(node, ast.Constant)
        and isinstance(node.value, (int, float))
        and not isinstance(node.value, bool)
    ):
        return float(node.value)
    if isinstance(node, ast.BinOp) and isinstance(node.op, ast.Div):
        return parse_number(ast.unparse(node.left)) / parse_number(
            ast.unparse(node.right)
        )
    if isinstance(node, ast.UnaryOp) and isinstance(node.op, ast.USub):
        return -parse_number(ast.unparse(node.operand))
    raise ValueError(f"Unsupported conversion factor: {expression}")


def generate_resources(resource_dir: Path) -> list[str]:
    rows = []
    for resource_file in sorted(resource_dir.glob("*/*.resw")):
        is_engine = resource_file.name == "CEngineStrings.resw"
        if resource_file.name not in ("CEngineStrings.resw", "Resources.resw"):
            continue
        for item in ET.parse(resource_file).getroot().findall("data"):
            key = item.attrib["name"]
            if not is_engine and not key.startswith(
                ("UnitName_", "UnitAbbreviation_", "CategoryName_")
            ):
                continue
            value = item.findtext("value", "")
            locale = resource_file.parent.name
            rows.append(
                f'    {{ "{locale}", {str(is_engine).lower()}, {quote_wide(key)}, {quote_wide(value)} }},'
            )
    if not rows:
        raise ValueError("No calculator resources found")
    return rows


def generate_units(loader: str, navigation: str) -> list[str]:
    constants = {
        key: int(value)
        for key, value in re.findall(r"private const int (\w+) = (\d+);", loader)
    }
    category_ids = {
        key: int(value)
        for key, value in re.findall(r"private const int (\w+)Id = (\d+);", navigation)
    }
    category_block = loader.split("var converterModes = new[]", 1)[1].split("};", 1)[0]
    categories = [
        category
        for category in re.findall(r"ViewMode\.(\w+)", category_block)
        if category != "Currency"
    ]
    if len(categories) != 12:
        raise ValueError("Review the desktop adapter for changed converter categories")

    factors: dict[str, float] = {}
    factor_block = loader.split("var conversionFactors =", 1)[1].split(
        "// Build explicit conversion data", 1
    )[0]
    for key, expression in re.findall(r"\{ (\w+), ([\d.eE+\-/ ]+) \}", factor_block):
        factors[key] = parse_number(expression)

    rows = ["inline const CategoryDefinition CATEGORY_DEFINITIONS[] = {"]
    for category in categories:
        supports_negative = str(category in ("Temperature", "Power", "Angle")).lower()
        rows.append(
            f'    {{ {category_ids[category]}, L"CategoryName_{category}Text", {supports_negative} }},'
        )
    rows.append("};\ninline const UnitDefinition UNIT_DEFINITIONS[] = {")
    unit_pattern = r'AddUnit\(ViewMode\.(\w+),\s*(\w+),\s*"([^"]*)",\s*"([^"]*)",\s*(\d+)([^;]*)\);'
    definitions = list(re.finditer(unit_pattern, loader))
    if len(definitions) != len(re.findall(r"AddUnit\(ViewMode\.", loader)):
        raise ValueError("A unit declaration could not be parsed")
    for match in definitions:
        category, unit, name, abbreviation, order, flags = match.groups()
        defaults = [flag.strip() for flag in flags.split(",") if flag.strip()]
        defaults += ["false"] * (3 - len(defaults))
        allowed_rules = {
            "false": 0,
            "true": 1,
            "useSI": 2,
            "useUSCustomary": 3,
            "useFahrenheit": 4,
            "!useFahrenheit": 5,
            "useWattInsteadOfKilowatt": 6,
            "!useWattInsteadOfKilowatt": 7,
            'useUSCustomary && _regionCode != "GB"': 8,
            'useUSCustomary && _regionCode == "GB"': 9,
        }
        source_rule, target_rule = (allowed_rules[flag] for flag in defaults[:2])
        factor = factors[unit] if category != "Temperature" else 1.0
        if factor <= 0 or defaults[2] not in ("true", "false"):
            raise ValueError(f"Invalid unit definition: {unit}")
        rows.append(
            f"    {{ {constants[unit]}, {category_ids[category]}, {quote_wide(name)}, "
            f"{quote_wide(abbreviation)}, {order}, {source_rule}, {target_rule}, {defaults[2]}, {factor!r} }},"
        )
    rows.append("};\ninline const TemperatureDefinition TEMPERATURE_DEFINITIONS[] = {")
    explicit_block = loader.split("var explicitConversions =", 1)[1].split(
        "// Build unit lookup", 1
    )[0]
    for source, body in re.findall(
        r"\[(Temperature_\w+)\].*?\{(.*?)\n                \},",
        explicit_block,
        re.DOTALL,
    ):
        for target, ratio, offset, offset_first in re.findall(
            r"\{ (Temperature_\w+), \(([^,]+), ([^,]+), (true|false)\) \}", body
        ):
            rows.append(
                f"    {{ {constants[source]}, {constants[target]}, {parse_number(ratio)!r}, {parse_number(offset)!r}, {offset_first} }},"
            )
    if (
        sum(
            row.startswith("    {")
            for row in rows[
                rows.index(
                    "};\ninline const TemperatureDefinition TEMPERATURE_DEFINITIONS[] = {"
                )
                + 1 :
            ]
        )
        != 9
    ):
        raise ValueError("Expected all nine temperature conversions")
    rows.append("};")
    return rows


def main(arguments: list[str]) -> None:
    resource_dir, loader_file, navigation_file, output_file = map(Path, arguments)
    resource_rows = generate_resources(resource_dir)
    declarations = """// Generated from the existing Windows resources and unit loader. Do not edit.
#pragma once
#include <string_view>
namespace Desktop::Generated
{
struct ResourceString { const char* locale; bool is_engine; const wchar_t* key; const wchar_t* value; };
struct CategoryDefinition { int id; std::wstring_view name_key; bool supports_negative; };
struct UnitDefinition { int id; int category_id; std::wstring_view name_key; std::wstring_view abbreviation_key;
    int order; int source_rule; int target_rule; bool whimsical; double factor; };
struct TemperatureDefinition { int from_id; int to_id; double ratio; double offset; bool offset_first; };
"""
    lines = [
        declarations,
        f"extern const ResourceString RESOURCE_STRINGS[{len(resource_rows)}];",
    ]
    lines.extend(
        generate_units(
            loader_file.read_text(encoding="utf-8"),
            navigation_file.read_text(encoding="utf-8"),
        )
    )
    lines.append("}")
    output_file.write_text("\n".join(lines) + "\n", encoding="utf-8")
    resource_source = [
        "// Generated from the Windows resource files. Do not edit.",
        '#include "DesktopResources.h"',
        "namespace Desktop::Generated",
        "{",
        "const ResourceString RESOURCE_STRINGS[] = {",
        *resource_rows,
        "};",
        "}",
    ]
    output_file.with_suffix(".cpp").write_text(
        "\n".join(resource_source) + "\n", encoding="utf-8"
    )


if __name__ == "__main__":
    main(sys.argv[1:])
