# Changelog

## [1.13.0](https://github.com/roberto-naharro/SchoolBuses/compare/v1.12.0...v1.13.0) (2026-06-28)


### Features

* implement panel placement controls for improved UI integration ([8fe7d38](https://github.com/roberto-naharro/SchoolBuses/commit/8fe7d383aae649cd2948c166b2c5e4367d360534))

## [1.12.0](https://github.com/roberto-naharro/SchoolBuses/compare/v1.11.0...v1.12.0) (2026-06-28)


### Features

* refactor external control integration for school bus scheduling ([a5f9597](https://github.com/roberto-naharro/SchoolBuses/commit/a5f9597ee4f3f393122e591ade78e7b55473e244))

## [1.11.0](https://github.com/roberto-naharro/SchoolBuses/compare/v1.10.0...v1.11.0) (2026-06-28)


### Features

* enhance docking logic for SchoolLinePanel to accommodate Transport Lines Manager ([78452bb](https://github.com/roberto-naharro/SchoolBuses/commit/78452bbb796d2e75de622431e67c40e1f5e157c5))

## [1.10.0](https://github.com/roberto-naharro/SchoolBuses/compare/v1.9.0...v1.10.0) (2026-06-28)


### Features

* integrate Real Time mod for dynamic school hours management ([be489b4](https://github.com/roberto-naharro/SchoolBuses/commit/be489b4a8e08584a8f50a09d26a7834c4f262f46))

## [1.9.0](https://github.com/roberto-naharro/SchoolBuses/compare/v1.8.0...v1.9.0) (2026-06-28)


### Features

* add dropdown UI component for selecting schools in ambiguous lines ([8e17d8c](https://github.com/roberto-naharro/SchoolBuses/commit/8e17d8c2655f42ec537c3289639ff608ff9cb6a8))

## [1.8.0](https://github.com/roberto-naharro/SchoolBuses/compare/v1.7.0...v1.8.0) (2026-06-27)


### Features

* implement nearest existing bus stop reuse logic to prevent duplicate stops ([ba59775](https://github.com/roberto-naharro/SchoolBuses/commit/ba597751cd6c85bc6c2dee3973ab9acd9eabafa7))
* introduce optional service hours for school lines, allowing bus spawning control ([96d5604](https://github.com/roberto-naharro/SchoolBuses/commit/96d56043ea9f2e2044c403e5a9b1e58959d0247c))

## [1.7.0](https://github.com/roberto-naharro/SchoolBuses/compare/v1.6.0...v1.7.0) (2026-06-26)


### Features

* add GetSchoolBuilding method and update API version to 3 ([88134d4](https://github.com/roberto-naharro/SchoolBuses/commit/88134d42d84daceab3c141be5eeb7bbb3b9e1c0a))

## [1.6.0](https://github.com/roberto-naharro/SchoolBuses/compare/v1.5.0...v1.6.0) (2026-06-17)


### Features

* add service hour restrictions for school buses, including configurable start and end times ([fc404db](https://github.com/roberto-naharro/SchoolBuses/commit/fc404db8be25a97a76cc75b706dfd275562d9a53))


### Bug Fixes

* restrict school lines to bus types only, hiding panel for other transit types ([dc86954](https://github.com/roberto-naharro/SchoolBuses/commit/dc86954e9c7ec19eddba44cf116c2a8442bc46e4))

## [1.5.0](https://github.com/roberto-naharro/SchoolBuses/compare/v1.4.0...v1.5.0) (2026-06-17)


### Features

* add day-only service option for school lines to prevent night operation ([03d0785](https://github.com/roberto-naharro/SchoolBuses/commit/03d0785a2568abd05bfd15a64656cdb023d07d02))
* update README and description to clarify school bus functionality and features ([d0184dd](https://github.com/roberto-naharro/SchoolBuses/commit/d0184dd2dc42e83930cec3108ae079cc905e7c28))

## [1.4.0](https://github.com/roberto-naharro/SchoolBuses/compare/v1.3.0...v1.4.0) (2026-06-11)


### Features

* implement path ownership and transit entry gate for school lines, excluding non-students from route planning ([ca1be17](https://github.com/roberto-naharro/SchoolBuses/commit/ca1be1730a8dea50a1368dc2949ebb9b4fb67b2c))

## [1.3.0](https://github.com/roberto-naharro/SchoolBuses/compare/v1.2.0...v1.3.0) (2026-06-10)


### Features

* add DockBeside utility to manage panel positioning and prevent clipping on screen edges ([8553392](https://github.com/roberto-naharro/SchoolBuses/commit/8553392af8c05793d2e378692cf9d98214bce559))

## [1.2.0](https://github.com/roberto-naharro/SchoolBuses/compare/v1.1.0...v1.2.0) (2026-06-10)


### Features

* implement free school transport feature, allowing students to ride without fare and eliminating maintenance costs for school lines ([2076f0d](https://github.com/roberto-naharro/SchoolBuses/commit/2076f0d63ea93c7de2d7c97c864b590b72b52ee7))
* implement school-as-depot functionality to spawn buses directly from schools ([58306d1](https://github.com/roberto-naharro/SchoolBuses/commit/58306d12bbb4220d5323b70e76cdf1b44ade203a))


### Bug Fixes

* enhance line spawning logic to prevent vehicle placement on incomplete paths ([4d5202f](https://github.com/roberto-naharro/SchoolBuses/commit/4d5202f36b7f1d6714748c9b1fad6220fb6c03a4))

## [1.1.0](https://github.com/roberto-naharro/SchoolBuses/compare/v1.0.0...v1.1.0) (2026-06-02)


### Features

* add TlmBridge for Transport Lines Manager integration and update documentation ([2cd0009](https://github.com/roberto-naharro/SchoolBuses/commit/2cd0009c07c7c74ab51229eb685fd58cb54c2c1a))


### Bug Fixes

* improve boarding logic to proactively evict ineligible riders from school stops ([1bbde4a](https://github.com/roberto-naharro/SchoolBuses/commit/1bbde4a8b7efbb061d0c22a8669b8f38ce9203fb))

## [1.0.0](https://github.com/roberto-naharro/SchoolBuses/compare/v0.1.0...v1.0.0) (2026-06-01)


### ⚠ BREAKING CHANGES

* enhance school route UI with icons and improved layout

### Features

* add IpteBridge for vehicle count management and integrate with RouteBuilder ([f23decd](https://github.com/roberto-naharro/SchoolBuses/commit/f23decd043651a623f77ca173e42a079e618c1f8))
* enhance route generation logic to prevent duplicate routes for schools ([44d12a2](https://github.com/roberto-naharro/SchoolBuses/commit/44d12a26c8d84a4a9c339e0839b29e8c2888742b))
* enhance route generation with experimental parameters and auto-regeneration ([d60d036](https://github.com/roberto-naharro/SchoolBuses/commit/d60d036f1767705422851dd986c66cf55643242c))
* enhance school route UI with icons and improved layout ([3b863db](https://github.com/roberto-naharro/SchoolBuses/commit/3b863db1327724ab477f3b08e0eb0581733a48e3))
* initial commit ([416adf6](https://github.com/roberto-naharro/SchoolBuses/commit/416adf64c8f042bcbc43439d0a2edf68302c9463))
* update description for Improved Public Transport Essentials compatibility ([27ed272](https://github.com/roberto-naharro/SchoolBuses/commit/27ed272b355e238887ee8a8b77e9fc00fcbf11ea))
* update README and description for clarity, enhance settings UI, and enforce student-only boarding ([8c5e577](https://github.com/roberto-naharro/SchoolBuses/commit/8c5e5778e7550ff45477305c1e771e9a6d4bdb01))
* update README for quick start instructions, enhance usage details, and improve workshop description ([8cb8445](https://github.com/roberto-naharro/SchoolBuses/commit/8cb844595720baead46927e84129217aac9d59f5))


### Bug Fixes

* update description handling in generate_description_vdf.py for proper escaping ([566451d](https://github.com/roberto-naharro/SchoolBuses/commit/566451d793d691ca9a78ce667bf8bac0e2a4cef5))

## Changelog
