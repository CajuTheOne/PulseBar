using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PulseBar;

internal static class L
{
	public static readonly (string Code, string Name)[] Languages = new(string, string)[11]
	{
		("en", "English"),
		("ru", "Русский"),
		("uk", "Українська"),
		("de", "Deutsch"),
		("fr", "Français"),
		("es", "Español"),
		("pt", "Português"),
		("it", "Italiano"),
		("pl", "Polski"),
		("zh", "中文"),
		("ja", "日本語")
	};

	private static Dictionary<string, string>? _map;

	private static readonly Dictionary<string, Dictionary<string, string>> All = new Dictionary<string, Dictionary<string, string>>
	{
		["ru"] = new Dictionary<string, string>
		{
			["Spacing from tray"] = "Отступ от трея",
			["One line"] = "Одна строка",
			["Two lines, vertical labels"] = "Две строки, вертикальные подписи",
			["Spacing between blocks"] = "Расстояние между блоками",
			["Normal color"] = "Цвет нормы",
			["Green"] = "Зелёный",
			["Blue"] = "Синий",
			["Text size"] = "Размер текста",
			["Small"] = "Мелкий",
			["Normal"] = "Обычный",
			["Large"] = "Крупный",
			["Update interval"] = "Обновление",
			["0.5 s"] = "0,5 с",
			["{0} s"] = "{0} с",
			["Graphics card"] = "Видеокарта",
			["Auto (discrete)"] = "Авто (дискретная)",
			["Show memory amount (GB)"] = "Показывать объём памяти (ГБ)",
			["Show VRAM"] = "Показывать VRAM",
			["VRAM load, %"] = "Загрузка VRAM, %",
			["Thresholds…"] = "Пороги…",
			["Hide in full-screen apps"] = "Скрывать в полноэкранных приложениях",
			["Start with Windows"] = "Запускать при входе в Windows",
			["CPU temperature: {0}"] = "Температура CPU: {0}",
			["Restart as administrator"] = "Перезапустить с правами администратора",
			["Task Manager"] = "Диспетчер задач",
			["Language"] = "Язык",
			["Exit"] = "Выход",
			["administrator rights required"] = "нужны права администратора",
			["AMD Ryzen only"] = "поддерживаются только AMD Ryzen",
			["PawnIO driver unavailable (error {0})"] = "драйвер PawnIO недоступен (ошибка {0})",
			["PawnIO rejected the module (error {0})"] = "PawnIO не принял модуль (ошибка {0})",
			["sensor not responding"] = "датчик не отвечает",
			["PulseBar — thresholds"] = "PulseBar — пороги",
			["Yellow from"] = "Жёлтый от",
			["Red from"] = "Красный от",
			["CPU load, %"] = "Загрузка CPU, %",
			["CPU temperature, °C"] = "Температура CPU, °C",
			["GPU load, %"] = "Загрузка GPU, %",
			["GPU temperature, °C"] = "Температура GPU, °C",
			["RAM load, %"] = "Загрузка RAM, %",
			["Below yellow is normal, from red is overload."] = "Ниже «жёлтого» — норма, от «красного» — перегруз.",
			["Defaults"] = "По умолчанию",
			["Cancel"] = "Отмена",
			["Could not change autostart (details in %LOCALAPPDATA%\\PulseBar\\pulsebar.log)."] = "Не удалось изменить автозапуск (подробности в журнале %LOCALAPPDATA%\\PulseBar\\pulsebar.log).",
			["sleep"] = "сон"
		},
		["uk"] = new Dictionary<string, string>
		{
			["Spacing from tray"] = "Відступ від трею",
			["One line"] = "Один рядок",
			["Two lines, vertical labels"] = "Два рядки, вертикальні підписи",
			["Spacing between blocks"] = "Відстань між блоками",
			["Normal color"] = "Колір норми",
			["Green"] = "Зелений",
			["Blue"] = "Синій",
			["Text size"] = "Розмір тексту",
			["Small"] = "Дрібний",
			["Normal"] = "Звичайний",
			["Large"] = "Великий",
			["Update interval"] = "Оновлення",
			["0.5 s"] = "0,5 с",
			["{0} s"] = "{0} с",
			["Graphics card"] = "Відеокарта",
			["Auto (discrete)"] = "Авто (дискретна)",
			["Show memory amount (GB)"] = "Показувати обсяг пам’яті (ГБ)",
			["Show VRAM"] = "Показувати VRAM",
			["VRAM load, %"] = "Використання VRAM, %",
			["Thresholds…"] = "Пороги…",
			["Hide in full-screen apps"] = "Ховати в повноекранних застосунках",
			["Start with Windows"] = "Запускати під час входу у Windows",
			["CPU temperature: {0}"] = "Температура CPU: {0}",
			["Restart as administrator"] = "Перезапустити з правами адміністратора",
			["Task Manager"] = "Диспетчер завдань",
			["Language"] = "Мова",
			["Exit"] = "Вихід",
			["administrator rights required"] = "потрібні права адміністратора",
			["AMD Ryzen only"] = "підтримуються лише AMD Ryzen",
			["PawnIO driver unavailable (error {0})"] = "драйвер PawnIO недоступний (помилка {0})",
			["PawnIO rejected the module (error {0})"] = "PawnIO не прийняв модуль (помилка {0})",
			["sensor not responding"] = "датчик не відповідає",
			["PulseBar — thresholds"] = "PulseBar — пороги",
			["Yellow from"] = "Жовтий від",
			["Red from"] = "Червоний від",
			["CPU load, %"] = "Навантаження CPU, %",
			["CPU temperature, °C"] = "Температура CPU, °C",
			["GPU load, %"] = "Навантаження GPU, %",
			["GPU temperature, °C"] = "Температура GPU, °C",
			["RAM load, %"] = "Використання RAM, %",
			["Below yellow is normal, from red is overload."] = "Нижче «жовтого» — норма, від «червоного» — перевантаження.",
			["Defaults"] = "За замовчуванням",
			["Cancel"] = "Скасувати",
			["Could not change autostart (details in %LOCALAPPDATA%\\PulseBar\\pulsebar.log)."] = "Не вдалося змінити автозапуск (подробиці в журналі %LOCALAPPDATA%\\PulseBar\\pulsebar.log).",
			["sleep"] = "сон"
		},
		["de"] = new Dictionary<string, string>
		{
			["Spacing from tray"] = "Abstand zum Infobereich",
			["One line"] = "Eine Zeile",
			["Two lines, vertical labels"] = "Zwei Zeilen, vertikale Beschriftung",
			["Spacing between blocks"] = "Abstand zwischen Blöcken",
			["Normal color"] = "Farbe für Normalbereich",
			["Green"] = "Grün",
			["Blue"] = "Blau",
			["Text size"] = "Textgröße",
			["Small"] = "Klein",
			["Normal"] = "Normal",
			["Large"] = "Groß",
			["Update interval"] = "Aktualisierung",
			["0.5 s"] = "0,5 s",
			["{0} s"] = "{0} s",
			["Graphics card"] = "Grafikkarte",
			["Auto (discrete)"] = "Automatisch (dediziert)",
			["Show memory amount (GB)"] = "Speichermenge anzeigen (GB)",
			["Show VRAM"] = "VRAM anzeigen",
			["VRAM load, %"] = "VRAM-Auslastung, %",
			["Thresholds…"] = "Schwellenwerte…",
			["Hide in full-screen apps"] = "In Vollbild-Apps ausblenden",
			["Start with Windows"] = "Mit Windows starten",
			["CPU temperature: {0}"] = "CPU-Temperatur: {0}",
			["Restart as administrator"] = "Als Administrator neu starten",
			["Task Manager"] = "Task-Manager",
			["Language"] = "Sprache",
			["Exit"] = "Beenden",
			["administrator rights required"] = "Administratorrechte erforderlich",
			["AMD Ryzen only"] = "nur AMD Ryzen unterstützt",
			["PawnIO driver unavailable (error {0})"] = "PawnIO-Treiber nicht verfügbar (Fehler {0})",
			["PawnIO rejected the module (error {0})"] = "PawnIO hat das Modul abgelehnt (Fehler {0})",
			["sensor not responding"] = "Sensor antwortet nicht",
			["PulseBar — thresholds"] = "PulseBar — Schwellenwerte",
			["Yellow from"] = "Gelb ab",
			["Red from"] = "Rot ab",
			["CPU load, %"] = "CPU-Auslastung, %",
			["CPU temperature, °C"] = "CPU-Temperatur, °C",
			["GPU load, %"] = "GPU-Auslastung, %",
			["GPU temperature, °C"] = "GPU-Temperatur, °C",
			["RAM load, %"] = "RAM-Auslastung, %",
			["Below yellow is normal, from red is overload."] = "Unter „Gelb“ – normal, ab „Rot“ – Überlastung.",
			["Defaults"] = "Standard",
			["Cancel"] = "Abbrechen",
			["Could not change autostart (details in %LOCALAPPDATA%\\PulseBar\\pulsebar.log)."] = "Autostart konnte nicht geändert werden (Details im Protokoll %LOCALAPPDATA%\\PulseBar\\pulsebar.log).",
			["sleep"] = "Ruhe"
		},
		["fr"] = new Dictionary<string, string>
		{
			["Spacing from tray"] = "Écart avec la zone de notification",
			["One line"] = "Une ligne",
			["Two lines, vertical labels"] = "Deux lignes, libellés verticaux",
			["Spacing between blocks"] = "Espacement entre les blocs",
			["Normal color"] = "Couleur normale",
			["Green"] = "Vert",
			["Blue"] = "Bleu",
			["Text size"] = "Taille du texte",
			["Small"] = "Petite",
			["Normal"] = "Normale",
			["Large"] = "Grande",
			["Update interval"] = "Actualisation",
			["0.5 s"] = "0,5 s",
			["{0} s"] = "{0} s",
			["Graphics card"] = "Carte graphique",
			["Auto (discrete)"] = "Auto (dédiée)",
			["Show memory amount (GB)"] = "Afficher la quantité de mémoire (Go)",
			["Show VRAM"] = "Afficher la VRAM",
			["VRAM load, %"] = "Utilisation VRAM, %",
			["Thresholds…"] = "Seuils…",
			["Hide in full-screen apps"] = "Masquer dans les applications plein écran",
			["Start with Windows"] = "Lancer au démarrage de Windows",
			["CPU temperature: {0}"] = "Température CPU : {0}",
			["Restart as administrator"] = "Redémarrer en tant qu’administrateur",
			["Task Manager"] = "Gestionnaire des tâches",
			["Language"] = "Langue",
			["Exit"] = "Quitter",
			["administrator rights required"] = "droits d’administrateur requis",
			["AMD Ryzen only"] = "AMD Ryzen uniquement",
			["PawnIO driver unavailable (error {0})"] = "pilote PawnIO indisponible (erreur {0})",
			["PawnIO rejected the module (error {0})"] = "PawnIO a refusé le module (erreur {0})",
			["sensor not responding"] = "le capteur ne répond pas",
			["PulseBar — thresholds"] = "PulseBar — seuils",
			["Yellow from"] = "Jaune à partir de",
			["Red from"] = "Rouge à partir de",
			["CPU load, %"] = "Charge CPU, %",
			["CPU temperature, °C"] = "Température CPU, °C",
			["GPU load, %"] = "Charge GPU, %",
			["GPU temperature, °C"] = "Température GPU, °C",
			["RAM load, %"] = "Utilisation RAM, %",
			["Below yellow is normal, from red is overload."] = "Sous le « jaune » : normal ; à partir du « rouge » : surcharge.",
			["Defaults"] = "Par défaut",
			["Cancel"] = "Annuler",
			["Could not change autostart (details in %LOCALAPPDATA%\\PulseBar\\pulsebar.log)."] = "Impossible de modifier le lancement automatique (détails dans %LOCALAPPDATA%\\PulseBar\\pulsebar.log).",
			["sleep"] = "veille"
		},
		["es"] = new Dictionary<string, string>
		{
			["Spacing from tray"] = "Separación del área de notificación",
			["One line"] = "Una línea",
			["Two lines, vertical labels"] = "Dos líneas, etiquetas verticales",
			["Spacing between blocks"] = "Espacio entre bloques",
			["Normal color"] = "Color normal",
			["Green"] = "Verde",
			["Blue"] = "Azul",
			["Text size"] = "Tamaño del texto",
			["Small"] = "Pequeño",
			["Normal"] = "Normal",
			["Large"] = "Grande",
			["Update interval"] = "Actualización",
			["0.5 s"] = "0,5 s",
			["{0} s"] = "{0} s",
			["Graphics card"] = "Tarjeta gráfica",
			["Auto (discrete)"] = "Automática (dedicada)",
			["Show memory amount (GB)"] = "Mostrar cantidad de memoria (GB)",
			["Show VRAM"] = "Mostrar VRAM",
			["VRAM load, %"] = "Uso de VRAM, %",
			["Thresholds…"] = "Umbrales…",
			["Hide in full-screen apps"] = "Ocultar en aplicaciones a pantalla completa",
			["Start with Windows"] = "Iniciar con Windows",
			["CPU temperature: {0}"] = "Temperatura de CPU: {0}",
			["Restart as administrator"] = "Reiniciar como administrador",
			["Task Manager"] = "Administrador de tareas",
			["Language"] = "Idioma",
			["Exit"] = "Salir",
			["administrator rights required"] = "se requieren permisos de administrador",
			["AMD Ryzen only"] = "solo AMD Ryzen",
			["PawnIO driver unavailable (error {0})"] = "controlador PawnIO no disponible (error {0})",
			["PawnIO rejected the module (error {0})"] = "PawnIO rechazó el módulo (error {0})",
			["sensor not responding"] = "el sensor no responde",
			["PulseBar — thresholds"] = "PulseBar — umbrales",
			["Yellow from"] = "Amarillo desde",
			["Red from"] = "Rojo desde",
			["CPU load, %"] = "Carga de CPU, %",
			["CPU temperature, °C"] = "Temperatura de CPU, °C",
			["GPU load, %"] = "Carga de GPU, %",
			["GPU temperature, °C"] = "Temperatura de GPU, °C",
			["RAM load, %"] = "Uso de RAM, %",
			["Below yellow is normal, from red is overload."] = "Por debajo del «amarillo»: normal; desde el «rojo»: sobrecarga.",
			["Defaults"] = "Predeterminado",
			["Cancel"] = "Cancelar",
			["Could not change autostart (details in %LOCALAPPDATA%\\PulseBar\\pulsebar.log)."] = "No se pudo cambiar el inicio automático (detalles en %LOCALAPPDATA%\\PulseBar\\pulsebar.log).",
			["sleep"] = "reposo"
		},
		["pt"] = new Dictionary<string, string>
		{
			["Spacing from tray"] = "Espaço até a área de notificação",
			["One line"] = "Uma linha",
			["Two lines, vertical labels"] = "Duas linhas, rótulos verticais",
			["Spacing between blocks"] = "Espaço entre blocos",
			["Normal color"] = "Cor normal",
			["Green"] = "Verde",
			["Blue"] = "Azul",
			["Text size"] = "Tamanho do texto",
			["Small"] = "Pequeno",
			["Normal"] = "Normal",
			["Large"] = "Grande",
			["Update interval"] = "Atualização",
			["0.5 s"] = "0,5 s",
			["{0} s"] = "{0} s",
			["Graphics card"] = "Placa de vídeo",
			["Auto (discrete)"] = "Automática (dedicada)",
			["Show memory amount (GB)"] = "Mostrar quantidade de memória (GB)",
			["Show VRAM"] = "Mostrar VRAM",
			["VRAM load, %"] = "Uso da VRAM, %",
			["Thresholds…"] = "Limites…",
			["Hide in full-screen apps"] = "Ocultar em aplicativos de tela cheia",
			["Start with Windows"] = "Iniciar com o Windows",
			["CPU temperature: {0}"] = "Temperatura da CPU: {0}",
			["Restart as administrator"] = "Reiniciar como administrador",
			["Task Manager"] = "Gerenciador de Tarefas",
			["Language"] = "Idioma",
			["Exit"] = "Sair",
			["administrator rights required"] = "requer privilégios de administrador",
			["AMD Ryzen only"] = "apenas AMD Ryzen",
			["PawnIO driver unavailable (error {0})"] = "driver PawnIO indisponível (erro {0})",
			["PawnIO rejected the module (error {0})"] = "o PawnIO rejeitou o módulo (erro {0})",
			["sensor not responding"] = "o sensor não responde",
			["PulseBar — thresholds"] = "PulseBar — limites",
			["Yellow from"] = "Amarelo a partir de",
			["Red from"] = "Vermelho a partir de",
			["CPU load, %"] = "Uso da CPU, %",
			["CPU temperature, °C"] = "Temperatura da CPU, °C",
			["GPU load, %"] = "Uso da GPU, %",
			["GPU temperature, °C"] = "Temperatura da GPU, °C",
			["RAM load, %"] = "Uso da RAM, %",
			["Below yellow is normal, from red is overload."] = "Abaixo do «amarelo»: normal; a partir do «vermelho»: sobrecarga.",
			["Defaults"] = "Padrão",
			["Cancel"] = "Cancelar",
			["Could not change autostart (details in %LOCALAPPDATA%\\PulseBar\\pulsebar.log)."] = "Não foi possível alterar a inicialização automática (detalhes em %LOCALAPPDATA%\\PulseBar\\pulsebar.log).",
			["sleep"] = "repouso"
		},
		["it"] = new Dictionary<string, string>
		{
			["Spacing from tray"] = "Distanza dall’area di notifica",
			["One line"] = "Una riga",
			["Two lines, vertical labels"] = "Due righe, etichette verticali",
			["Spacing between blocks"] = "Spazio tra i blocchi",
			["Normal color"] = "Colore normale",
			["Green"] = "Verde",
			["Blue"] = "Blu",
			["Text size"] = "Dimensione testo",
			["Small"] = "Piccolo",
			["Normal"] = "Normale",
			["Large"] = "Grande",
			["Update interval"] = "Aggiornamento",
			["0.5 s"] = "0,5 s",
			["{0} s"] = "{0} s",
			["Graphics card"] = "Scheda video",
			["Auto (discrete)"] = "Automatica (dedicata)",
			["Show memory amount (GB)"] = "Mostra quantità di memoria (GB)",
			["Show VRAM"] = "Mostra VRAM",
			["VRAM load, %"] = "Uso VRAM, %",
			["Thresholds…"] = "Soglie…",
			["Hide in full-screen apps"] = "Nascondi nelle app a schermo intero",
			["Start with Windows"] = "Avvia con Windows",
			["CPU temperature: {0}"] = "Temperatura CPU: {0}",
			["Restart as administrator"] = "Riavvia come amministratore",
			["Task Manager"] = "Gestione attività",
			["Language"] = "Lingua",
			["Exit"] = "Esci",
			["administrator rights required"] = "servono i diritti di amministratore",
			["AMD Ryzen only"] = "solo AMD Ryzen",
			["PawnIO driver unavailable (error {0})"] = "driver PawnIO non disponibile (errore {0})",
			["PawnIO rejected the module (error {0})"] = "PawnIO ha rifiutato il modulo (errore {0})",
			["sensor not responding"] = "il sensore non risponde",
			["PulseBar — thresholds"] = "PulseBar — soglie",
			["Yellow from"] = "Giallo da",
			["Red from"] = "Rosso da",
			["CPU load, %"] = "Carico CPU, %",
			["CPU temperature, °C"] = "Temperatura CPU, °C",
			["GPU load, %"] = "Carico GPU, %",
			["GPU temperature, °C"] = "Temperatura GPU, °C",
			["RAM load, %"] = "Uso RAM, %",
			["Below yellow is normal, from red is overload."] = "Sotto il «giallo»: normale; dal «rosso»: sovraccarico.",
			["Defaults"] = "Predefiniti",
			["Cancel"] = "Annulla",
			["Could not change autostart (details in %LOCALAPPDATA%\\PulseBar\\pulsebar.log)."] = "Impossibile modificare l’avvio automatico (dettagli in %LOCALAPPDATA%\\PulseBar\\pulsebar.log).",
			["sleep"] = "riposo"
		},
		["pl"] = new Dictionary<string, string>
		{
			["Spacing from tray"] = "Odstęp od zasobnika",
			["One line"] = "Jedna linia",
			["Two lines, vertical labels"] = "Dwie linie, pionowe etykiety",
			["Spacing between blocks"] = "Odstęp między blokami",
			["Normal color"] = "Kolor normy",
			["Green"] = "Zielony",
			["Blue"] = "Niebieski",
			["Text size"] = "Rozmiar tekstu",
			["Small"] = "Mały",
			["Normal"] = "Normalny",
			["Large"] = "Duży",
			["Update interval"] = "Odświeżanie",
			["0.5 s"] = "0,5 s",
			["{0} s"] = "{0} s",
			["Graphics card"] = "Karta graficzna",
			["Auto (discrete)"] = "Automatycznie (dedykowana)",
			["Show memory amount (GB)"] = "Pokazuj ilość pamięci (GB)",
			["Show VRAM"] = "Pokazuj VRAM",
			["VRAM load, %"] = "Użycie VRAM, %",
			["Thresholds…"] = "Progi…",
			["Hide in full-screen apps"] = "Ukrywaj w aplikacjach pełnoekranowych",
			["Start with Windows"] = "Uruchamiaj z systemem Windows",
			["CPU temperature: {0}"] = "Temperatura CPU: {0}",
			["Restart as administrator"] = "Uruchom ponownie jako administrator",
			["Task Manager"] = "Menedżer zadań",
			["Language"] = "Język",
			["Exit"] = "Zakończ",
			["administrator rights required"] = "wymagane uprawnienia administratora",
			["AMD Ryzen only"] = "tylko AMD Ryzen",
			["PawnIO driver unavailable (error {0})"] = "sterownik PawnIO niedostępny (błąd {0})",
			["PawnIO rejected the module (error {0})"] = "PawnIO odrzucił moduł (błąd {0})",
			["sensor not responding"] = "czujnik nie odpowiada",
			["PulseBar — thresholds"] = "PulseBar — progi",
			["Yellow from"] = "Żółty od",
			["Red from"] = "Czerwony od",
			["CPU load, %"] = "Obciążenie CPU, %",
			["CPU temperature, °C"] = "Temperatura CPU, °C",
			["GPU load, %"] = "Obciążenie GPU, %",
			["GPU temperature, °C"] = "Temperatura GPU, °C",
			["RAM load, %"] = "Użycie RAM, %",
			["Below yellow is normal, from red is overload."] = "Poniżej „żółtego” — norma, od „czerwonego” — przeciążenie.",
			["Defaults"] = "Domyślne",
			["Cancel"] = "Anuluj",
			["Could not change autostart (details in %LOCALAPPDATA%\\PulseBar\\pulsebar.log)."] = "Nie udało się zmienić autostartu (szczegóły w %LOCALAPPDATA%\\PulseBar\\pulsebar.log).",
			["sleep"] = "sen"
		},
		["zh"] = new Dictionary<string, string>
		{
			["Spacing from tray"] = "与托盘的间距",
			["One line"] = "单行",
			["Two lines, vertical labels"] = "双行，竖排标签",
			["Spacing between blocks"] = "块间距",
			["Normal color"] = "正常颜色",
			["Green"] = "绿色",
			["Blue"] = "蓝色",
			["Text size"] = "文字大小",
			["Small"] = "小",
			["Normal"] = "正常",
			["Large"] = "大",
			["Update interval"] = "刷新间隔",
			["0.5 s"] = "0.5 秒",
			["{0} s"] = "{0} 秒",
			["Graphics card"] = "显卡",
			["Auto (discrete)"] = "自动（独立显卡）",
			["Show memory amount (GB)"] = "显示内存用量 (GB)",
			["Show VRAM"] = "显示显存",
			["VRAM load, %"] = "显存占用，%",
			["Thresholds…"] = "阈值…",
			["Hide in full-screen apps"] = "在全屏应用中隐藏",
			["Start with Windows"] = "登录 Windows 时启动",
			["CPU temperature: {0}"] = "CPU 温度：{0}",
			["Restart as administrator"] = "以管理员身份重新启动",
			["Task Manager"] = "任务管理器",
			["Language"] = "语言",
			["Exit"] = "退出",
			["administrator rights required"] = "需要管理员权限",
			["AMD Ryzen only"] = "仅支持 AMD Ryzen",
			["PawnIO driver unavailable (error {0})"] = "PawnIO 驱动不可用（错误 {0}）",
			["PawnIO rejected the module (error {0})"] = "PawnIO 拒绝了模块（错误 {0}）",
			["sensor not responding"] = "传感器无响应",
			["PulseBar — thresholds"] = "PulseBar — 阈值",
			["Yellow from"] = "黄色起始值",
			["Red from"] = "红色起始值",
			["CPU load, %"] = "CPU 负载，%",
			["CPU temperature, °C"] = "CPU 温度，°C",
			["GPU load, %"] = "GPU 负载，%",
			["GPU temperature, °C"] = "GPU 温度，°C",
			["RAM load, %"] = "内存占用，%",
			["Below yellow is normal, from red is overload."] = "低于“黄色”为正常，达到“红色”为过载。",
			["Defaults"] = "默认值",
			["Cancel"] = "取消",
			["Could not change autostart (details in %LOCALAPPDATA%\\PulseBar\\pulsebar.log)."] = "无法更改自动启动（详见日志 %LOCALAPPDATA%\\PulseBar\\pulsebar.log）。",
			["sleep"] = "休眠"
		},
		["ja"] = new Dictionary<string, string>
		{
			["Spacing from tray"] = "通知領域との間隔",
			["One line"] = "1 行",
			["Two lines, vertical labels"] = "2 行（縦書きラベル）",
			["Spacing between blocks"] = "ブロックの間隔",
			["Normal color"] = "通常時の色",
			["Green"] = "緑",
			["Blue"] = "青",
			["Text size"] = "文字サイズ",
			["Small"] = "小",
			["Normal"] = "標準",
			["Large"] = "大",
			["Update interval"] = "更新間隔",
			["0.5 s"] = "0.5 秒",
			["{0} s"] = "{0} 秒",
			["Graphics card"] = "グラフィックス カード",
			["Auto (discrete)"] = "自動（専用 GPU）",
			["Show memory amount (GB)"] = "メモリ使用量を表示 (GB)",
			["Show VRAM"] = "VRAM を表示",
			["VRAM load, %"] = "VRAM 使用率, %",
			["Thresholds…"] = "しきい値…",
			["Hide in full-screen apps"] = "全画面アプリでは非表示",
			["Start with Windows"] = "Windows のサインイン時に起動",
			["CPU temperature: {0}"] = "CPU 温度: {0}",
			["Restart as administrator"] = "管理者として再起動",
			["Task Manager"] = "タスク マネージャー",
			["Language"] = "言語",
			["Exit"] = "終了",
			["administrator rights required"] = "管理者権限が必要です",
			["AMD Ryzen only"] = "AMD Ryzen のみ対応",
			["PawnIO driver unavailable (error {0})"] = "PawnIO ドライバーを利用できません（エラー {0}）",
			["PawnIO rejected the module (error {0})"] = "PawnIO がモジュールを拒否しました（エラー {0}）",
			["sensor not responding"] = "センサーが応答しません",
			["PulseBar — thresholds"] = "PulseBar — しきい値",
			["Yellow from"] = "黄色の開始値",
			["Red from"] = "赤の開始値",
			["CPU load, %"] = "CPU 使用率, %",
			["CPU temperature, °C"] = "CPU 温度, °C",
			["GPU load, %"] = "GPU 使用率, %",
			["GPU temperature, °C"] = "GPU 温度, °C",
			["RAM load, %"] = "RAM 使用率, %",
			["Below yellow is normal, from red is overload."] = "「黄色」未満は正常、「赤」以上は過負荷です。",
			["Defaults"] = "既定値",
			["Cancel"] = "キャンセル",
			["Could not change autostart (details in %LOCALAPPDATA%\\PulseBar\\pulsebar.log)."] = "自動起動を変更できませんでした（詳細はログ %LOCALAPPDATA%\\PulseBar\\pulsebar.log）。",
			["sleep"] = "休止"
		}
	};

	private static readonly Dictionary<string, string> Ru;

	private static readonly Dictionary<string, string> Uk;

	public static string Code { get; private set; } = "en";

	public static void Set(string code)
	{
		Code = (Languages.Any(((string Code, string Name) l) => l.Code == code) ? code : "en");
		_map = ((Code == "en") ? null : All[Code]);
	}

	public static string T(string en)
	{
		if (_map == null || !_map.TryGetValue(en, out string value))
		{
			return Extra(en);
		}
		return value;
	}

	public static string F(string en, object arg)
	{
		return string.Format(CultureInfo.InvariantCulture, T(en), arg);
	}

	private static string Extra(string en)
	{
		string code = Code;
		Dictionary<string, string> dictionary = ((code == "ru") ? Ru : ((!(code == "uk")) ? null : Uk));
		Dictionary<string, string> dictionary2 = dictionary;
		if (dictionary2 == null || !dictionary2.TryGetValue(en, out var value))
		{
			return en;
		}
		return value;
	}

	static L()
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>();
		dictionary["Keyboard and mouse…"] = "Клавиатура и мышь…";
		dictionary["Profile"] = "Профиль";
		dictionary["Show tray icon"] = "Значок в трее";
		dictionary["PulseBar — keyboard and mouse"] = "PulseBar — клавиатура и мышь";
		dictionary["Close G HUB"] = "Закрыть G HUB";
		dictionary["Profiles (M1–M3 switch the first three)"] = "Профили (M1–M3 — первые три)";
		dictionary["Rename"] = "Переименовать";
		dictionary["Switch automatically for programs:"] = "Включать для программ:";
		dictionary["e.g. game.exe, photoshop.exe"] = "например: game.exe, photoshop.exe";
		dictionary["Keyboard"] = "Клавиатура";
		dictionary["Logo"] = "Логотип";
		dictionary["lights off"] = "выключить";
		dictionary["Profile colour"] = "Цвет профиля";
		dictionary["Dim when idle"] = "Простой";
		dictionary["min without use → brightness"] = "мин без нажатий → яркость";
		dictionary["0 minutes = never dim. Any key press or mouse move brings the profile brightness back."] = "0 минут — не приглушать. Любое нажатие клавиши или движение мыши возвращает яркость профиля.";
		dictionary["Key colours"] = "Цвет клавиш";
		dictionary["Colour for selected…"] = "Цвет выбранным…";
		dictionary["Use profile colour"] = "Как у профиля";
		dictionary["Select all"] = "Выбрать все";
		dictionary["Clear selection"] = "Снять выбор";
		dictionary["click keys to select them"] = "щёлкните по клавишам, чтобы выбрать";
		dictionary["selected: {0}"] = "выбрано: {0}";
		dictionary["Changes are sent right away; Apply sends the whole profile to the keyboard again."] = "Изменения отправляются сразу; «Применить» заново отправляет на клавиатуру весь профиль.";
		dictionary["Buttons G1–G5"] = "Кнопки G1–G5";
		dictionary["Unassigned G-keys do nothing."] = "Неназначенные G-клавиши ничего не делают.";
		dictionary["Apply"] = "Применить";
		dictionary["Battery"] = "Заряд";
		dictionary["Yellow at or below"] = "Жёлтый при ≤";
		dictionary["Red at or below"] = "Красный при ≤";
		dictionary["Keyboard charge, %"] = "Клавиатура, %";
		dictionary["Mouse charge, %"] = "Мышь, %";
		dictionary["Profile"] = "Профиль";
		dictionary["Delete"] = "Удалить";
		dictionary["+ Add profile…"] = "+ Добавить профиль…";
		dictionary["Buttons"] = "Кнопки";
		dictionary["Lighting"] = "Подсветка";
		dictionary["Mouse"] = "Мышь";
		dictionary["Control Logitech devices (instead of G HUB)"] = "Управлять устройствами Logitech (вместо G HUB)";
		dictionary["Close"] = "Закрыть";
		dictionary["Nothing"] = "Ничего";
		dictionary["Key combination"] = "Сочетание клавиш";
		dictionary["Type text"] = "Набрать текст";
		dictionary["Run program / open"] = "Запустить / открыть";
		dictionary["Macro"] = "Макрос";
		dictionary["Off"] = "Выключено";
		dictionary["Color"] = "Цвет";
		dictionary["Breathing"] = "Дыхание";
		dictionary["Color cycle"] = "Перелив цветов";
		dictionary["Programs"] = "Программы";
		dictionary["All files"] = "Все файлы";
		dictionary["Key combination: click the field and press the keys (Backspace clears)."] = "Сочетание клавиш: щёлкните по полю и нажмите клавиши (Backspace — очистить).";
		dictionary["Macro: steps separated by ';', e.g.  Ctrl+C; wait 100; text Hello; Enter; run notepad"] = "Макрос: шаги через «;», например:  Ctrl+C; wait 100; text Привет; Enter; run notepad";
		dictionary["Steps: a key combination, wait <ms>, text <text>, down <key>, up <key>, run <program>."] = "Шаги: сочетание клавиш, wait <мс>, text <текст>, down <клавиша>, up <клавиша>, run <программа>.";
		dictionary["Media keys: PlayPause, Next, Prev, Mute, VolUp, VolDown."] = "Мультимедиа: PlayPause, Next, Prev, Mute, VolUp, VolDown.";
		dictionary["press keys…"] = "нажмите клавиши…";
		dictionary["text to type"] = "текст для набора";
		dictionary["program, file, folder or URL"] = "программа, файл, папка или ссылка";
		dictionary["Brightness"] = "Яркость";
		dictionary["Changes apply right away. Settings are kept in the profile and restored when the keyboard wakes up."] = "Изменения применяются сразу. Настройки хранятся в профиле и восстанавливаются, когда клавиатура просыпается.";
		dictionary["Period, s:"] = "Период, с:";
		dictionary["Logitech mice"] = "Мыши Logitech";
		dictionary["Windows mouse settings (all mice)"] = "Настройки мыши Windows (для всех мышей)";
		dictionary["Pointer speed"] = "Скорость указателя";
		dictionary["Enhance pointer precision (acceleration)"] = "Повышенная точность (ускорение)";
		dictionary["Wheel scrolls lines"] = "Строк за щелчок колеса";
		dictionary["Double-click speed"] = "Двойной щелчок";
		dictionary["Swap left and right buttons"] = "Поменять левую и правую кнопки";
		dictionary["M325 / M305 have no DPI or lighting settings; Windows applies these to every mouse."] = "У M325 и M305 нет настроек DPI и подсветки; эти настройки Windows действуют на все мыши.";
		dictionary["New profile"] = "Новый профиль";
		dictionary["Profile {0}"] = "Профиль {0}";
		dictionary["Rename profile"] = "Переименовать профиль";
		dictionary["Delete profile \"{0}\"?"] = "Удалить профиль «{0}»?";
		dictionary["Logitech control is off"] = "Управление Logitech выключено";
		dictionary["No Logitech G keyboard found"] = "Клавиатура Logitech G не найдена";
		dictionary["asleep or off"] = "спит или выключена";
		dictionary["battery"] = "заряд";
		dictionary["connected"] = "подключена";
		dictionary["not found"] = "не найдены";
		dictionary["auto profile: {0}"] = "автопрофиль: {0}";
		dictionary["G HUB is running: it fights for the keyboard and doubles the macros."] = "Запущен G HUB: он перехватывает клавиатуру и дублирует макросы.";
		dictionary["Close G HUB now? It will start again with Windows unless its autostart is turned off."] = "Закрыть G HUB сейчас? При следующем входе в Windows он запустится снова, если не отключить его автозапуск.";
		dictionary["Cancel"] = "Отмена";
		dictionary["(auto)"] = "(авто)";
		dictionary["No mouse found"] = "Мышь не найдена";
		dictionary["battery level not available"] = "заряд недоступен";
		dictionary["Other mice"] = "Другие мыши";
		dictionary["none"] = "нет";
		dictionary["These Windows settings apply to every mouse. DPI and lighting of a Razer mouse stay in its own memory / Razer software."] = "Эти настройки Windows действуют на все мыши. DPI и подсветка мыши Razer хранятся в её памяти и в программе Razer.";
		dictionary["Show keyboard and mouse battery"] = "Заряд клавиатуры и мыши на панели";
		dictionary["Update to {0}"] = "Обновить до {0}";
		dictionary["Downloading {0}…"] = "Загрузка {0}…";
		dictionary["Get beta versions"] = "Получать бета-версии";
		dictionary["Embed in the taskbar"] = "Встраивать в панель задач";
		dictionary["Could not install the update (details in %LOCALAPPDATA%\\PulseBar\\pulsebar.log)."] = "Не удалось установить обновление (подробности в %LOCALAPPDATA%\\PulseBar\\pulsebar.log).";
		dictionary["PulseBarSensor service is not running"] = "служба PulseBarSensor не запущена";
		dictionary["PawnIO is not installed"] = "PawnIO не установлен";
		dictionary["unsupported CPU"] = "процессор не поддерживается";
		dictionary["Show Claude limits"] = "Лимиты Claude на строке";
		dictionary["Load"] = "Загрузка";
		dictionary["Temperature"] = "Температура";
		dictionary["Used"] = "Занято";
		dictionary["5 hours"] = "5 часов";
		dictionary["7 days"] = "7 дней";
		dictionary["Updated"] = "Обновлено";
		dictionary["{0} min ago"] = "{0} мин назад";
		dictionary["Claude app"] = "приложение Claude";
		dictionary["reset in {0}"] = "сброс через {0}";
		dictionary["{0} d"] = "{0} д";
		dictionary["{0} h"] = "{0} ч";
		dictionary["{0} min"] = "{0} мин";
		dictionary["60 s"] = "60 с";
		Ru = dictionary;
		Uk = new Dictionary<string, string>
		{
			["Keyboard and mouse…"] = "Клавіатура і миша…",
			["Profile"] = "Профіль",
			["Show tray icon"] = "Значок у треї",
			["Update to {0}"] = "Оновити до {0}",
			["Downloading {0}…"] = "Завантаження {0}…",
			["Get beta versions"] = "Отримувати бета-версії",
			["Embed in the taskbar"] = "Вбудовувати в панель завдань",
			["Could not install the update (details in %LOCALAPPDATA%\\PulseBar\\pulsebar.log)."] = "Не вдалося встановити оновлення (подробиці в %LOCALAPPDATA%\\PulseBar\\pulsebar.log).",
			["PulseBarSensor service is not running"] = "служба PulseBarSensor не запущена",
			["PawnIO is not installed"] = "PawnIO не встановлено",
			["unsupported CPU"] = "процесор не підтримується",
			["Show Claude limits"] = "Ліміти Claude на рядку",
			["Load"] = "Навантаження",
			["Temperature"] = "Температура",
			["Used"] = "Зайнято",
			["5 hours"] = "5 годин",
			["7 days"] = "7 днів",
			["Updated"] = "Оновлено",
			["{0} min ago"] = "{0} хв тому",
			["Claude app"] = "застосунок Claude",
			["reset in {0}"] = "скидання через {0}",
			["{0} d"] = "{0} д",
			["{0} h"] = "{0} год",
			["{0} min"] = "{0} хв",
			["60 s"] = "60 с"
		};
	}
}
