## Настройка
Создайте файл `config.yml` в директории с утилитой. Пример конфигурации описан в файле `config.example.yml`
```yaml
## Пример заполнения файла config.yml
SyncChain:
   - source: # Описание Source PG/Feed, пакеты отсюда будут сравниваться с DEST PG/Feed (config.example.yml#6)
        url: "http://localhost" # URL адрес инстанса Source PG
        apiKey: "30a4c00a5ce95d038577fea1d671a567" # API_KEY с правами на фид описанный ниже ( View/Download,Add/Repackage,Overwrite/Delete )
        feed: "first-feed" # Имя Source Feed
     destination: # Описание DEST PG/Feed, пакеты отсюда будут сравниваться с Source PG/Feed (config.example.yml#1)
        url: "http://localhost:8080" # URL адрес инстанса Dest PG
        apiKey: "51960d3631983c7f7bcf2e20ae1e60e" # API_KEY с правами на фид описанный ниже ( View/Download,Add/Repackage,Overwrite/Delete )
        feed: "second-feed" # Имя Dest Feed
     type: "upack" # тип синхронизируемого фида. Доступные "nuget", "upack", "assets".

   - source: # Тоже что и выше.
        url: "http://localhost:8081"
        apiKey: "0dae18212a6f41ec8e2aaae8624614cf91d63205"
        feed: "first-sec-feed"
     destination:
        url: "http://localhost:8083"
        apiKey: "28e868cd710575c58881cf24ba358d9ecfb93af7"
        feed: "third-sec-feed"
     type: "nuget"

# тут можно добавить ещё несколько цепочек синхронизации
#  - source:
#      url: ""
#      apiKey: ""
#      feed: ""
#    destination:
#      url: ""
#      apiKey: ""
#      feed: ""
#    type: "nuget"

# Ниже конфиг один на все цепочки
ProceedPackageLimit: 10 # Кол-во обрабатываемых параллельно пакетов. Снижение этого параметра снижает общую нагрузку на ресурсы хоста

timeout: # Конфигурация таймаутов
   webRequestTimeout: 15 # Таймаут обращений к апи
   iterationTimeout: 20 # Пауза между синхронизациями
   syncTimeout: 120 # Общий таймаут для операции синхронизации

retention: # Конфигурация отчистки версий старше указанного лимита
   enabled: true # Включение
   dry-run: false # Отчистка без удаления пакетов, только логирование
   versionLimit: 2 # Кол-во версий, которые будут храниться, всё что старше будет удалено.
```
# Алгоритм работы

1. Чтение конфигурационного файла `config.yml`, который содержит информацию об исходном и целевом серверах (URL, API ключи и фиды), а также таймауты для запросов.

2. Настройка логирования: если указан путь к файлу логов, настраивается логирование так, чтобы записи логов выводились как в файл, так и в консоль.

3. Очистка указанной директории, удаляя все её содержимое, чтобы подготовить её для временного хранения загруженных пакетов.

4. Запуск основного цикла работы, который продолжается до получения сигнала на завершение (например, SIGTERM).
    - Цикличная обработка каждой цепочки

5. Выполнение основной логики в каждом цикле:

   5.1. Загрузка списка пакетов с исходного сервера:
    - Отправка GET запроса на исходный сервер по адресу `source.url/upack/source.feed/packages` с использованием API ключа.
    - Парсинг ответа, содержащего список пакетов в формате JSON, в структуру данных.

   5.2. Загрузка списка пакетов с целевого сервера:
    - Отправка GET запроса на целевой сервер по адресу `destination.url/upack/destination.feed/packages` с использованием API ключа.
    - Парсинг ответа в структуру данных.

   5.3. Сравнение списков пакетов с исходного и целевого серверов:
    - Определение, какие пакеты отсутствуют на целевом сервере (их нужно синхронизировать) и какие пакеты есть на целевом сервере, но отсутствуют на исходном (их нужно удалить).
    - Если включен `retention`, то при сравнении пакетов, пакеты версии которых, по порядку, выше чем  `retention.versionLimit`, будет помечены для пропуска.

   5.4. Синхронизация пакетов:
    - Для каждого пакета, который нужно синхронизировать:
        - Скачивание пакета с исходного сервера:
            - Отправка GET запроса на исходный сервер для скачивания конкретного пакета по адресу `source.url/syncChain.Type/source.feed/download/group/name/version`.
            - Загрузка пакета в указанную директорию на локальной машине.
        - Загрузка пакета на целевой сервер:
            - Отправка POST запроса на целевой сервер для загрузки пакета по адресу `destination.url/syncChain.Type/destination.feed/upload`.
            - Удаление пакета из локальной директории после успешной загрузки.

   5.5. Удаление пакетов с целевого сервера:
    - Повторно запрашивается список пакетов целевого сервера (5.2)
    - Отправка POST запроса на удаление пакетов, версия которых, по порядку, выше чем  `retention.versionLimit`

   5.6. Ожидание перед следующей итерацией на время, указанное в конфигурации (`iterationTimeout`).

# Ключи запуска
```bash
  -c string
        path to config file (default "config.yml")
  -l string
        path to logfile
  -p string
        path to save downloaded packages (default "./packages")
```