# Consysto Files — страница в Microsoft Store

Черновик полей Partner Center. Поля — в том порядке, в каком их спрашивает форма «Описание в Store» (Store listing).
Языки описания: русский (ru-RU) и английский (en-US). Имя продукта в магазине: **Consysto Files**.

## Общие поля (для обоих языков)

- **Категория:** Производительность (Productivity), подкатегория нет. Запасной вариант — «Утилиты и инструменты».
- **Цена:** бесплатно. Доступность: все рынки, где разрешено.
- **Политика конфиденциальности:** https://github.com/egorcha174/ConsystoFiles/blob/main/docs/PRIVACY.md
- **Сайт:** https://github.com/egorcha174/ConsystoFiles
- **Поддержка:** https://github.com/egorcha174/ConsystoFiles/issues
- **Авторские права:** © 2026 Egor Chayka. Based on Files by Files Community (MIT).
- **Возрастной рейтинг (анкета IARC):** нет насилия, покупок, общения между пользователями, сбора данных → ожидаемо 3+.
- **Системные требования:** Windows 10 версии 1809 и новее, Windows 11; x64.

## Обоснование особых разрешений (Submission options → Restricted capabilities)

Английский — его читают проверяющие.

- **runFullTrust** — Consysto Files is a desktop file manager built on the Windows App SDK. It runs as a full-trust process to list, copy, move, rename and delete files anywhere the user browses, to read shell properties and thumbnails, and to start helper processes (CAD geometry reader, archive tools, Git, the built-in terminal).
- **broadFileSystemAccess** — As a file manager, the app has to open any folder the user navigates to and operate on its files, without a picker for each location.
- **allowElevation** — Some file operations the user starts (for example, in Program Files or on system drives) require administrator rights; Windows asks the user to confirm each elevation.
- **unvirtualizedResources** — Used so that file operations and settings in the user's AppData folder act on the real locations rather than a virtualized copy.
- **packageQuery** — Used to show which installed app opens a file type and to list apps in the "Open with" menu.
- Removed in the Store build: **packageManagement** (only the sideloaded build uses it for self-updates; the Store updates the app).

Это те же права, с которыми опубликован исходный Files (files-community); для него магазин их принял.

## en-US

**Short title:** Consysto Files

**Description**

A free file manager for Windows 11 that shows what's inside your work files, right in the folder.

Consysto Files is built on Files, the modern open-source file manager, and adds what a design engineer needs every day: previews of drawings and 3D models without the programs that made them.

• Drawings and models in the folder. DWG and DXF drawings, STEP and IGES models, Autodesk Inventor parts, assemblies and drawings, SolidWorks, KOMPAS-3D, Fusion, Siemens NX, CATIA, Rhino, FreeCAD — thumbnails in the folder and a model you can rotate in the preview pane.
• An Inventor assembly opens like a folder: you see the parts it is made of, and missing references are shown separately.
• Part number, material, mass and program version in the columns — sort and filter a folder of parts like a table.
• 3D printing: G-code, binary .bgcode, FlashPrint .gx and sliced .gcode.3mf projects show the slicer's picture, print time, filament, layer height, nozzle and printer.
• Illustrator and CorelDRAW files are shown by the picture stored inside them.
• Collections over your own folders: photos, music, books and drawings from different places in one gallery; select, copy, paste and rename right there.
• Book catalogs (OPDS) and sharing your books to a phone reader over Wi-Fi.
• Two panes side by side for those who grew up with Total Commander, folder comparison and sync, a built-in terminal tab, a torrent client.
• Two looks: Finder-style by default, or the regular Windows 11 style.

No accounts, no ads, no telemetry. Free, and the source code is open.

**What's new in this version**

First release in the Microsoft Store.

**Product features** (по одному на строку, до 200 знаков)

- Thumbnails and 3D preview for DWG, DXF, STEP, IGES, Inventor, SolidWorks, KOMPAS-3D and more
- Inventor assemblies open like folders; missing references shown separately
- Columns for part number, material, mass and program version
- Print time, filament and slicer picture for G-code, .bgcode and .gcode.3mf
- Collections of photos, books and drawings over your own folders
- Two panes, folder compare and sync, terminal tab, torrent client
- No accounts, no ads, no telemetry; open source

**Keywords** (до 7): file manager, CAD viewer, DWG, STEP, Inventor, 3D printing, Explorer

## ru-RU

**Название:** Consysto Files

**Описание**

Бесплатный файловый менеджер для Windows 11, который показывает, что внутри рабочих файлов, прямо в папке.

Consysto Files сделан на основе Files — современного файлового менеджера с открытым кодом — и добавляет то, что конструктору нужно каждый день: просмотр чертежей и 3D-моделей без программ, в которых они сделаны.

• Чертежи и модели прямо в папке. Чертежи DWG и DXF, модели STEP и IGES, детали, сборки и чертежи Autodesk Inventor, SolidWorks, КОМПАС-3D, Fusion, Siemens NX, CATIA, Rhino, FreeCAD — миниатюры в папке и модель, которую можно вращать, в панели просмотра.
• Сборка Inventor открывается как папка: видно, из каких деталей она состоит, ненайденные ссылки показаны отдельно.
• Обозначение, материал, масса и версия программы в колонках — папку деталей можно сортировать и фильтровать, как таблицу.
• 3D-печать: G-code, двоичный .bgcode, .gx из FlashPrint и нарезанные проекты .gcode.3mf показывают картинку из слайсера, время печати, пластик, высоту слоя, сопло и принтер.
• Файлы Illustrator и CorelDRAW видны картинкой, сохранённой внутри файла.
• Коллекции поверх ваших папок: фото, музыка, книги и чертежи из разных мест в одной галерее; выделять, копировать, вставлять и переименовывать можно прямо там.
• Каталоги книг (OPDS) и раздача своих книг на телефон по Wi-Fi.
• Две панели рядом для тех, кто вырос на Total Commander, сравнение и синхронизация папок, вкладка с терминалом, торрент-клиент.
• Два вида: в духе Finder по умолчанию или обычный Windows 11.

Без учётных записей, рекламы и слежки. Бесплатно, исходный код открыт.

**Что нового**

Первый выпуск в Microsoft Store.

**Возможности**

- Миниатюры и 3D-просмотр DWG, DXF, STEP, IGES, Inventor, SolidWorks, КОМПАС-3D и других форматов
- Сборки Inventor открываются как папки, ненайденные ссылки видны отдельно
- Колонки «Обозначение», «Материал», «Масса», «Версия»
- Время печати, пластик и картинка слайсера для G-code, .bgcode и .gcode.3mf
- Коллекции фото, книг и чертежей поверх своих папок
- Две панели, сравнение и синхронизация папок, терминал, торренты
- Без учётных записей, рекламы и телеметрии; открытый код

**Ключевые слова:** файловый менеджер, просмотр чертежей, DWG, STEP, Inventor, 3D-печать, проводник

## Скриншоты

Магазин просит от 1 до 10 снимков, не меньше 1366×768. Готовые из README (docs/screenshots): cad-formats, cad-preview,
print-3d, drawings-dxf, collection, two-panes, terminal — русский интерфейс; en/01…12 — английский. Для ru-RU брать русские,
для en-US — английские. Перед загрузкой проверить размер (часть снимков 1175×753 — меньше минимума, их переснять или подложить поле).
