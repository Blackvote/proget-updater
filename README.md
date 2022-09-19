# Updater

Утилита для копирования/синхронизации пакетов из центрального ProGet (Deeplay) в ProGet эксплуатации.

## Documentations

Документация доступна в Confluence:
    - "GitLab. Репозиторий и CI/CD" https://confluence.getcom.pw/pages/viewpage.action?pageId=38377967
    - "ProGet. 3. Установка утилиты updater" https://confluence.getcom.pw/pages/viewpage.action?pageId=45652780

## Getting Started

Разворачиваем утилиту из upack-пакета, например updater-2.0.0.upack во временный каталог.
https://proget.netsrv.it:38443/feeds/Updater

Копируем файлы из каталога под используемую платформу:
    - Windows: copy package\win-x64\*.* Z:\updater\
    - Linux:   cp package/linux-x64/* /srv/updater/

Редактируем файл конфигурации 'config.json'.

Под Linux делаем файл запускаемым: `chmod 0750 /srv/updater/updater`

Запускаем приложение 'updater.exe' от имени Администратора.

Временные файлы создаются в каталоге ./updater/ в зависимости от платформы.
    - Windows: путь из переменной окружения TMP или TEMP или USERPROFILE или каталог Windows (C:\Windows\TEMP\).
    - Linux: путь из переменной окружения TMPDIR или используется путь /tmp/ по-умолчанию.

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

Для ApiKey необходимо в настройках ProGet (administration/api-keys) сгенерировать ключи для нужных фидов.

Права для ключей на стороне source-proget:
- View/Download.

Права на стороне destination-proget:
- View/Download;
- Add/Repackage;
- Overwrite/Delete.

## Like Windows Service

Для установки в виде сервиса в ОС Windows используйте утилиту NSSM (Non-Sucking Service Manager) http://nssm.cc/

Обязательно версию NSSM не ниже 2.24-103 и версию updater не ниже 2.0.0 !

nssm 2.24-103-gdee49fc (2017-05-16) [0722c8a775deb4a1460d1750088916f4f5951773] http://nssm.cc/ci/nssm-2.24-103-gdee49fc.zip

Для правильной установки сервиса выполните следующие команды:

```
C:\bin\nssm-2.24-103-gdee49fc\win64\nssm.exe install Updater "C:\bin\updater\updater.exe"
C:\bin\nssm-2.24-103-gdee49fc\win64\nssm.exe set Updater Description Feeds synchronization between ProGets
C:\bin\nssm-2.24-103-gdee49fc\win64\nssm.exe set Updater AppKillProcessTree 0
C:\bin\nssm-2.24-103-gdee49fc\win64\nssm.exe set Updater AppExit Default Restart
C:\bin\nssm-2.24-103-gdee49fc\win64\nssm.exe set Updater AppExit 0 Exit
C:\bin\nssm-2.24-103-gdee49fc\win64\nssm.exe set Updater AppExit 10 Ignore
C:\bin\nssm-2.24-103-gdee49fc\win64\nssm.exe set Updater AppEvents Stop/Pre "powershell Stop-Process -Name updater -Force"
```

где
    - C:\bin\nssm-2.24-103-gdee49fc\win64\ = каталог установки утилиты NSSM
    - C:\bin\updater = каталог установки утилиты updater
    - Updater = название сервиса
