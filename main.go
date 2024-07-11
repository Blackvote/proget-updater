package main

import (
	"context"
	"flag"
	"github.com/rs/zerolog/log"
	"net/http"
	_ "net/http/pprof"
	"os"
	"os/signal"
	"sync"
	"syscall"
	"time"
)

var (
	configFile  *string = new(string)
	savePath    *string = new(string)
	logFilePath *string = new(string)
	debug       *bool   = new(bool)
)

const maxRetries = 5

func init() {
	flag.StringVar(configFile, "c", "config.yml", "path to config file")
	flag.StringVar(savePath, "p", "./packages", "path to save downloaded packages")
	flag.StringVar(logFilePath, "l", "", "path to logfile")
	flag.BoolVar(debug, "debug", false, "debug mode")
	flag.Parse()
}

func main() {
	go func() {
		log.Print(http.ListenAndServe("localhost:6060", nil))
	}()
	if *logFilePath != "" {
		logFile, err := setupLogging(*logFilePath)
		if err != nil {
			log.Error().Err(err).Msg("Failed to open log file")
			return
		}
		defer logFile.Close()
	}

	log.Info().Msgf("Clean %s", *savePath)
	err := createDeleteDirectoryContents(*savePath)
	if err != nil {
		log.Error().Err(err).Msg("Error deleting directory contents")
	} else {
		log.Info().Msg("Directory contents deleted successfully")
	}

	stop := make(chan os.Signal, 1)
	signal.Notify(stop, syscall.SIGINT, syscall.SIGTERM)

	for {
		config, err := readConfig(*configFile)
		if err != nil {
			log.Fatal().Err(err).Msg("Failed to read config")
		}
		ctx, cancel := context.WithTimeout(context.Background(), time.Duration(config.Timeout.SyncTimeout)*time.Second)

		go func() {
			<-stop
			log.Fatal().Msg("Received stop signal.")
			cancel()
		}()

		err = run(ctx)
		if err != nil {
			log.Error().Err(err).Msg("Error syncing")
		}

		cancel()

		select {
		case <-stop:
			log.Info().Msg("Received stop signal, close loop.")
			return
		case <-time.After((time.Duration(config.Timeout.IterationTimeout) / 2) * time.Second):
			log.Info().Msgf("Starting new iteration after pause. %d seconds", config.Timeout.IterationTimeout)
		}
	}
}

func run(parentCtx context.Context) error {
	mutex := sync.Mutex{}
	mutex.Lock()
	defer mutex.Unlock()

	log.Info().Msg("Application start")

	config, err := readConfig(*configFile)
	if err != nil {
		log.Fatal().Err(err).Msg("Failed to read config")
	}

	for _, chain := range config.SyncChain {
		select {
		case <-parentCtx.Done():
			log.Warn().Msgf("Timeout or cancel signal received, exiting run. Timeout: %d seconds", config.Timeout.SyncTimeout)
			return parentCtx.Err()
		default:
			sourcePackages, err := getPackages(parentCtx, chain.Source, config.Timeout)
			if err != nil {
				log.Error().Err(err).Msg("Failed to get packages from source")
				continue
			}

			destPackages, err := getPackages(parentCtx, chain.Destination, config.Timeout)
			if err != nil {
				log.Error().Err(err).Msg("Failed to get packages from destination")
				continue
			}

			err = SyncPackages(parentCtx, config, chain, sourcePackages, destPackages, *savePath)
			if err != nil {
				log.Error().Err(err).Msg("Failed to SyncChain packages")
				continue
			}

			if config.Retention.Enabled {
				log.Info().Msgf("Start retention")
				destPackages, err = getPackages(parentCtx, chain.Destination, config.Timeout)
				if err != nil {
					log.Error().Err(err).Msg("Failed to get packages from destination")
					continue
				}

				err = retention(parentCtx, config, chain, destPackages)
				if err != nil {
					log.Error().Err(err).Msg("Retention failed")
					continue
				}
			}
		}
	}
	log.Info().Msgf("Pause %d seconds", config.Timeout.IterationTimeout)
	select {
	case <-parentCtx.Done():
		log.Warn().Msgf("Timeout or cancel signal received, exiting run. Timeout: %d seconds", config.Timeout.SyncTimeout)
		return parentCtx.Err()
	case <-time.After((time.Duration(config.Timeout.IterationTimeout) / 2) * time.Second):
		return nil
	}
}
