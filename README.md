# Updater

Утилита для копирования пакетов из центрального ProGet (Deeplay) в ProGet эксплуатации.

## Getting Started

Запуск должен осуществляться от имени администратора.

Редактируем файл конфигурации 'config.json' и запускаем приложение 'updater.exe'.

Временные файлы создаются в каталоге ./updater/ в зависимости от платформы.
Windows:
    - The path specified by the TMP environment variable.
    - The path specified by the TEMP environment variable.
    - The path specified by the USERPROFILE environment variable.
    - The Windows directory.
Linux:
    - The path specified by the TMPDIR environment variable.
    - If the path is not specified in the TMPDIR environment variable, the default path /tmp/ is used.

Логи пишутся
    - в локальный Seq (http://127.0.0.1:5341)
    - в файлы в каталог 'Logs', который создается в каталоге запуска утилиты. Ротация файлов каждый час.

### Prerequisites

Конфигурация приложения в файле 'config.json'.
!!!Обратите внимание на обязательное наличие слешей в конце адресов!!!
```
{
  "SourceProget": {
    "Address": "https://proget.netsrv.it:38443/",
    "FeedName": "Neo",
    "ApiKey": "ikvx7RRCAXXLTvJ-uxJw"
  },
  "DestProget": {
    "Address": "https://pg.orpo.netsrv.pw/",
    "FeedName": "Neo",
    "ApiKey": "RezQyJmfcWxxCawMiy1g"
  }
}

```
также есть возможность синхронизировать сразу несколько фидов, пример конфига:
```
[
{
  "SourceProget": {
    "Address": "https://proget.netsrv.it:38443/",
    "FeedName": "sourceFeed",
    "ApiKey": "someApiKey"
  },
  "DestProget": {
    "Address": "https://pg.orpo.netsrv.pw/",
    "FeedName": "destFeed",
    "ApiKey": "someApiKey"
  }
},
{
  "SourceProget": {
    "Address": "https://proget.netsrv.it:38443/",
    "FeedName": "someFeed",
    "ApiKey": "someApiKey"
  },
  "DestProget": {
    "Address": "https://pg.orpo.netsrv.pw/",
    "FeedName": "drugoiFeed",
    "ApiKey": "someApiKey"
  }
}
]

```

Для ApiKey необходимо в настройках ProGet (administration/api-keys) выставить права:
- "Native API"
- "Feed API"

## Self-Update

Утилита самообновляется на последную версию из фида/канала Updater в SourceProget первой пары Source-Target в файле 'config.json'.
Обычно это https://proget.netsrv.it:38443/feeds/Updater
