## Настройка
Создайте файл `config.yml` в директории с утилитой. Пример конфигурации описан в файле `config.example.yml`
```yaml
## Пример заполнения файла config.yml
SyncChain:
   - source: # Описание Source PG/Feed, пакеты отсюда будут сравниваться с DEST PG/Feed
        url: "http://localhost" # URL адрес инстанса Source PG
        apiKey: "30a4c00a5ce95d038577" # API_KEY с правами на фид описанный ниже ( View/Download,Add/Repackage,Overwrite/Delete )
        feed: "first-feed" # Имя Source Feed
     destination: # Описание DEST PG/Feed, пакеты отсюда будут сравниваться с Source PG/Feed
        url: "http://localhost:8080" # URL адрес инстанса Dest PG
        apiKey: "51960d3631983c7f7bcf2" # API_KEY с правами на фид описанный ниже ( View/Download,Add/Repackage,Overwrite/Delete )
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
proceedPackageLimit: 10 # Кол-во обрабатываемых параллельно пакетов. Снижение этого параметра снижает общую нагрузку на ресурсы хоста
proceedPackageVersion: 1 # Кол-во версий пакета обрабатываемых параллельно. Снижение этого параметра снижает общую нагрузку на ресурсы хоста


timeout: # Конфигурация таймаутов
   webRequestTimeout: 15 # Таймаут обращений к апи
   iterationTimeout: 20 # Пауза между синхронизациями
   syncTimeout: 120 # Общий таймаут для операции синхронизации
   MaxRetries: 5 # Кол-во повторов запросов вернувших не ожидаемый status-code

retention: # Конфигурация отчистки версий старше указанного лимита
   enabled: false # Включение
   dry-run: true # Отчистка без удаления пакетов, только логирование
   versionLimit: 2 # Кол-во версий, которые будут храниться, всё что старше будет удалено.
```
# Алгоритм работы

1. Чтение конфигурационного файла `config.yml`, который содержит информацию об исходном и целевом серверах (URL, API ключи и фиды), а также таймауты для запросов.

2. Настройка логирования: если указан путь к файлу логов, настраивается логирование так, чтобы записи логов выводились как в файл, так и в консоль.

3. Очистка директории пакетов, удаляет содержимое, чтобы подготовить для временного хранения загруженных пакетов.

4. Выполнение основной логики, цикличная обработка каждой цепочки (syncChain), которая продолжается до получения сигнала на завершение (например, SIGTERM). 

   4.1. Загрузка списка пакетов с исходного сервера:
    - Отправка GET запроса на исходный сервер по адресу `syncChain.source.url/syncChain.Type/syncChain.source.feed/packages` с использованием API ключа.
    - Парсинг ответа, содержащего список пакетов в формате JSON, в структуру данных.

   4.2. Загрузка списка пакетов с целевого сервера:
    - Отправка GET запроса на целевой сервер по адресу `syncChain.destination.url/syncChain.Type/syncChain.destination.feed/packages` с использованием API ключа.
    - Парсинг ответа в структуру данных.

   4.3. Сравнение списков пакетов с исходного и целевого серверов:
    - Определение, какие пакеты присутствуют в `source`, отсутствуют в `destination`.
    - Кол-во элементов списка обрезается до `proceedPackageLimit`
    - Кол-во версий каждого пакета обрезается до `proceedPackageVersion`
    - Если включен `retention`, то при сравнении пакетов, пакеты версии которых, по порядку, выше чем  `retention.versionLimit`, будет помечены для пропуска.

   4.4. Синхронизация пакетов:
    - Для каждой версии, каждого пакета, который нужно синхронизировать:
        - Скачивание пакета с исходного сервера:
          - Отправка GET запроса на исходный сервер для скачивания конкретного пакета по адресу `syncChain.source.url/syncChain.Type/syncChain.source.feed/download/group/name/version`.
          - Полученный `body` записывается в директорию пакетов с именем `name.version`
        - Загрузка пакета на целевой сервер:
          - Чтение содержимого файла из файла пакетов с именем `name.version`
          - Отправка PUT запроса на целевой сервер для загрузки содержимого файла по адресу `syncChain.destination.url/syncChain.Type/syncChain.destination.feed/upload`.

   4.5. Retention:
    - Повторно запрашивается список пакетов целевого сервера (5.2)
    - Отправка POST запроса на удаление пакетов, версия которых, по порядку, выше чем  `retention.versionLimit`

   4.6. Ожидание перед следующей итерацией на время, указанное в конфигурации (`iterationTimeout`).

# Ключи запуска
```bash
  -c string
        path to config file (default "config.yml")
  -l string
        path to logfile
  -p string
        path to save downloaded packages (default "./packages")
  --debug
        print some debug information
```