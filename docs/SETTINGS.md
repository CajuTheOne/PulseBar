# PulseBar — настройки, файлы, команды

## Где что лежит

| Что | Где |
|---|---|
| Программа | `C:\Program Files\PulseBar\` (`PulseBar.exe` — строка, `PulseBar.Sensor.exe` — служба) |
| Исходники | https://github.com/CajuTheOne/PulseBar, рабочая копия `N:\AI\Tools\PulseBar` |
| Настройки | `%APPDATA%\PulseBar\settings.ini` |
| Журналы | строка — `%LOCALAPPDATA%\PulseBar\pulsebar.log`, служба — `%ProgramData%\PulseBar\sensor.log` (очищаются при 64 КБ) |
| Автозапуск | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, значение `PulseBar` |
| Служба | `PulseBarSensor` (LocalSystem, автоматически), канал `\\.\pipe\PulseBar.Sensor` |

## Меню (правый клик по строке)

| Пункт | Что делает |
|---|---|
| One line / Two lines, vertical labels | вид строки |
| Normal color → Green / Blue | цвет значений в норме |
| Text size → Small / Normal / Large | масштаб шрифтов 0,88 / 1 / 1,15 |
| Spacing between blocks → 6…40 px | промежуток между блоками в двухстрочном виде |
| Spacing from tray → 0…100 px, шаг 10 | отступ от области уведомлений (стрелки скрытых значков) |
| Update interval → 0,5 / 1 / 2 / 5 s | частота обновления |
| Graphics card → Auto / список | какую видеокарту показывать (если их несколько) |
| Show VRAM | показывать блок VRAM |
| Show memory amount (GB) | показывать объём в ГБ у RAM и VRAM |
| Thresholds… | окно порогов |
| Language | язык интерфейса (11 языков) |
| Hide in full-screen apps | прятать строку, когда открыто полноэкранное приложение |
| Update to x.y | появляется, когда на GitHub есть новая версия: скачать и установить (UAC) |
| Embed in the taskbar | строка — дочернее окно панели задач (по умолчанию); выкл — окно поверх панели, как в 1.x |
| Start with Windows | автозапуск при входе (ключ Run текущего пользователя) |
| Get beta versions | предлагать и бета-версии (пре-релизы GitHub) |
| CPU temperature: … | появляется, если температуры CPU нет, — причина (служба не запущена, нет PawnIO, процессор не поддерживается) |
| Task Manager | открыть Диспетчер задач (то же — двойной клик по строке) |
| Exit | выход |

Изменения сохраняются сразу.

## settings.ini

Формат `ключ=значение`, по одной настройке на строку. Неизвестные ключи игнорируются; некорректные значения
заменяются значениями по умолчанию или ограничиваются диапазоном.

| Ключ | По умолчанию | Значения |
|---|---|---|
| `layout` | `0` | `0` — одна строка, `1` — две строки (старые `2`, `3` тоже читаются как две строки) |
| `language` | `en` | `en`, `ru`, `uk`, `de`, `fr`, `es`, `pt`, `it`, `pl`, `zh`, `ja` |
| `blueNormal` | `0` | `1` — синий цвет нормы |
| `textSize` | `1` | `0` мелкий, `1` обычный, `2` крупный |
| `intervalMs` | `1000` | 250…10000 |
| `hideFullscreen` | `1` | `0`/`1` |
| `showRamGb` | `1` | объём в ГБ у RAM и VRAM |
| `showVram` | `1` | блок VRAM |
| `groupGap` | `20` | 0…80, px при 100 % |
| `trayGap` | `0` | 0…100, округляется до десятков |
| `embed` | `1` | `1` — внутри панели задач, `0` — поверх панели |
| `beta` | `0` | `1` — получать бета-версии |
| `trayIcon` | `1` | значок в трее |
| `showBattery` | `1` | заряд клавиатуры и мыши на строке |
| `gpu` | пусто | `VEN_xxxx&DEV_xxxx` выбранной видеокарты; пусто — автоматически дискретная |
| `cpuLoad` | `60,85` | пороги «жёлтый от,красный от», % |
| `cpuTemp` | `75,90` | °C |
| `gpuLoad` | `60,85` | % |
| `gpuTemp` | `70,83` | °C |
| `vram` | `70,85` | % |
| `ram` | `70,85` | % |

Ручные правки файла применяются после перезапуска PulseBar.

## Командная строка

| Команда | Что делает |
|---|---|
| `PulseBar.exe` | обычный запуск (второй экземпляр ждёт до 3 с и выходит, если первый не закрылся) |
| `PulseBar.exe --dump` | одно измерение всех датчиков в `%TEMP%\PulseBar-dump.txt` (диагностика) |
| `PulseBar.exe --autostart on` / `off` | включить/выключить автозапуск текущего пользователя |
| `PulseBar.Sensor.exe --dump` | температура CPU напрямую через PawnIO (от администратора) |
| `PulseBar.Sensor.exe --console` | служба в окне консоли (отладка, от администратора; саму службу перед этим остановить) |

## Сборка, установка, удаление

См. [README](../README.md#сборка): `build.ps1` собирает программу и установщик Inno Setup
(`installer\PulseBar.iss`), GitHub Actions публикует его по тегу `v*`.

Установщик (`PulseBar-Setup-<версия>.exe`, права администратора): закрывает PulseBar и службу, удаляет PulseBar 1.x
(задачу `\AI Tools\PulseBar` и папку `C:\Program Files\AI Tools\PulseBar`), при необходимости ставит .NET 9 Desktop
Runtime и PawnIO, копирует файлы в `C:\Program Files\PulseBar`, создаёт/обновляет службу `PulseBarSensor`,
включает автозапуск и запускает строку. Ключи для тихой установки: `/SILENT` или `/VERYSILENT`, `/SUPPRESSMSGBOXES`,
`/NORESTART`; так же его запускает обновление из программы. Удаление — «Параметры → Приложения» (настройки в
`%APPDATA%\PulseBar` остаются).

Требования: Windows 10/11 x64, .NET 9 Desktop Runtime; для температуры CPU — AMD Ryzen (Zen) или Intel Core
и драйвер PawnIO. Для сборки — .NET SDK 9 и Inno Setup 6.
