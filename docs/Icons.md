# Application icons

The shared desktop application uses the owned [icon.svg](../src/CalculatorDesktop/assets/icon.svg) artwork.
Its [MIT license](../src/CalculatorDesktop/assets/LICENSE.txt) credits Sabino.
Uno renders the vector and generates platform icon resources from it.

| Asset | Sizes or purpose |
| --- | --- |
| `icon.svg` | Shared vector source for Uno/Skia |
| `calculator-*.png` | 16, 24, 32, 44, 48, 64, 128, 256, 512, and 1024 pixels |
| `Calculator.icns` | macOS icon container, including 1x and 2x representations |
| `Calculator.ico` | Windows frames at 16, 24, 32, 48, 64, 128, and 256 pixels |

The generated assets accompany the source. Ordinary builds do not require `rsvg-convert`.
macOS publishing creates the application bundle icon; Windows builds embed the executable icon.
After publishing, `cmake --install` installs the Linux desktop entry, launcher, SVG, and hicolor PNG sizes.
The original UWP application's packaged resources remain under `src/Calculator/Assets`.

## Regeneration

Install Python and librsvg's `rsvg-convert`. On macOS with Malt:

```sh
mt install librsvg
python3 src/CalculatorDesktop/generate_icons.py \
  --source src/CalculatorDesktop/assets/icon.svg \
  --output src/CalculatorDesktop/assets
```

The script renders each PNG, checks dimensions, and writes the ICNS and ICO containers.
Inspect the ICNS representations with macOS's `iconutil`:

```sh
iconutil -c iconset src/CalculatorDesktop/assets/Calculator.icns \
  -o out/Calculator.iconset
```
