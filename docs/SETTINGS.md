# PulseBar — настройки, файлы, команды

## Где что лежит

| Что | Где |
|---|---|
| Программа | `C:\Program Files\AI Tools\PulseBar\` (`PulseBar.exe`, `PulseBar.dll`, документация в `docs\`) |
| Исходники | `D:\Work\PulseBar\` |
| Настройки | `%APPDATA%\PulseBar\settings.ini` |
| Журнал | `%LOCALAPPDATA%\PulseBar\pulsebar.log` (очищается при достижении 64 КБ) |
| Автозапуск | Планировщик заданий → папка `AI Tools` → задача `PulseBar` |

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
| Start with Windows | создать/удалить задачу автозапуска (без прав администратора — через UAC) |
| Restart as administrator | появляется, если нет температуры CPU из-за прав |
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
| `PulseBar.exe --autostart on` / `off` | создать/удалить задачу Планировщика (нужны права администратора) |

## Сборка, установка, удаление

```powershell
cd D:\Work\PulseBar
dotnet publish -c Release -o publish
powershell -ExecutionPolicy Bypass -File install.ps1              # установка/обновление (окно UAC)
powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall   # удаление (настройки в %APPDATA% остаются)
```

Установщик останавливает работающий PulseBar, копирует сборку, README и `docs` в `C:\Program Files\AI Tools\PulseBar`,
создаёт задачу автозапуска и запускает её.

Требования: Windows 10/11, .NET 9 Desktop Runtime; для температуры CPU — AMD Ryzen (Zen) и драйвер PawnIO.
Для сборки — .NET SDK 9.
