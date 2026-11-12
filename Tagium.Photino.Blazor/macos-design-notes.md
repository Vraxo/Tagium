# macOS-inspired interface references

This is an opt-in web approximation, not native AppKit or SwiftUI. The host OS
continues to provide the window frame and native dialogs. The chosen theme,
corner style, zoom, keyboard interactions, and panel widths remain independent
preferences.

## References evaluated

- Apple's [macOS Human Interface Guidelines](https://developer.apple.com/design/human-interface-guidelines/designing-for-macos), [sidebars](https://developer.apple.com/design/human-interface-guidelines/sidebars), [toolbars](https://developer.apple.com/design/human-interface-guidelines/toolbars), and [materials](https://developer.apple.com/design/human-interface-guidelines/materials). In particular, the materials guidance reserves Liquid Glass for navigation and controls, not the content layer. A WebView cannot reproduce the system material exactly.
- [Puppertino](https://github.com/codedgar/Puppertino), MIT, examined at `71faf544f9815355d525c77d4af7f1ae32d5ce3c`. Its desktop sidebar uses inset rows, restrained selection, and colored icons. Its CSS also defines its own global colors and class system. Importing the complete framework would conflict with Tagium's existing themes, controls and CSS isolation; these principles are implemented in Tagium's own styles instead. No Puppertino code or assets are bundled.
- [React Finder](https://github.com/arkarmin-gg/react-finder), MIT, examined at `a4bda20ce7859f84ebcd529dd728b02580d50d1a`. Its split-panel structure is a useful layout reference, but its React state and simulated filesystem cannot replace Tagium's Blazor components or library. No code or assets are bundled.
- [macOS Sequoia Web Simulator](https://github.com/andrewzyf/macos) has no license declared in its GitHub repository metadata. Do not copy its code or assets without permission.

## Validation still needed

Compare the opt-in mode in the Windows WebView against a specific version of Finder in light and dark mode, including selected and focused items, empty/populated libraries, narrow widths, high zoom, both tag sidebar styles, and custom themes. Apple fonts, symbols, and native materials are not shipped by this project; use appropriately licensed replacements rather than copying system assets. The design kit and macOS screenshots can help with the visual review, but their terms should be checked before redistributing any assets.

## Supplied Finder references

The supplied screenshots show a light, pre-Liquid-Glass Finder treatment: a
neutral sidebar with small section labels and blue icons, a white toolbar and
content area, restrained gray sidebar selection, an icon grid with centered
multiline labels, and subtly alternating list rows. They are visual references,
not evidence of a particular macOS release. Tagium keeps its own library
navigation and real commands instead of reproducing Finder-only actions. Frame,
menu bar, system dialogs, and dark-mode fidelity remain outside these references.
