package main

import (
	"context"
	"flag"
	"fmt"
	"github.com/rs/zerolog/log"
	_ "net/http/pprof"
	"net/url"
	"os"
	"os/signal"
	"strings"
	"sync"
	"syscall"
	"time"
)

var (
	configFile  = new(string)
	savePath    = new(string)
	logFilePath = new(string)
	debug       = new(bool)
)

func init() {
	flag.StringVar(configFile, "c", "config.yml", "path to config file")
	flag.StringVar(savePath, "p", "./packages", "path to save downloaded packages")
	flag.StringVar(logFilePath, "l", "", "path to logfile")
	flag.BoolVar(debug, "debug", false, "debug mode")
	flag.Parse()
}

func main() {
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
		if config.Retention.Enabled && !config.Retention.DryRun {
			if config.ProceedPackageVersion > config.Retention.VersionLimit {
				config.ProceedPackageVersion = config.Retention.VersionLimit
			}
		}

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
		_, err := url.ParseRequestURI(chain.Source.URL)
		if err != nil {
			fmt.Println("Invalid source URI:", err)
		}

		_, err = url.ParseRequestURI(chain.Destination.URL)
		if err != nil {
			fmt.Println("Invalid dest URI:", err)
		}

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

			syncPackages, err := getPackagesToSync(config, chain, sourcePackages, destPackages)
			if err != nil {
				log.Error().Err(err).Msg("Failed to SyncChain packages")
				continue
			}

			if *debug {
				log.Debug().Msgf("syncPackage = %d", len(syncPackages))
			}

			if len(syncPackages) > config.ProceedPackageLimit {
				syncPackages = syncPackages[:config.ProceedPackageLimit]
				newSourcePackages := make([]Package, len(syncPackages))
				copy(newSourcePackages, syncPackages)
				syncPackages = newSourcePackages
			}

			for i := range syncPackages {
				if len(sourcePackages[i].Versions) > 0 {
					sourcePackages[i].Versions = []string{sourcePackages[i].Versions[0]}
				}
			}

			log.Info().Msgf("Will sync %d packages with %d versions", len(syncPackages), config.ProceedPackageVersion)

			var packageList strings.Builder
			for _, pkg := range syncPackages {
				packageList.WriteString(fmt.Sprintf("%s/%s: %s | ", pkg.Group, pkg.Name, strings.Join(pkg.Versions, " ")))
			}
			log.Info().Str("url", chain.Destination.URL).Msg(packageList.String())

			var wg sync.WaitGroup

			errCh := make(chan error, len(syncPackages))

			for _, pkg := range syncPackages {
				for _, version := range pkg.Versions {
					wg.Add(1)
					go func(pkg Package, version string) {
						defer wg.Done()
						err := downloadAndUploadPackage(parentCtx, config, chain, pkg, version, *savePath)
						if err != nil {
							errCh <- fmt.Errorf("failed to sync package %s:%s, error: %w", pkg.Name, version, err)
						}
					}(pkg, version)
				}
			}

			wg.Wait()
			close(errCh)

			for err := range errCh {
				log.Error().Err(err).Msg("Error occurred during package sync")
				return err
			}

			if config.Retention.Enabled && chain.Type != "assets" {
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
