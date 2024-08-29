package main

import (
	"context"
	"flag"
	"fmt"
	"github.com/rs/zerolog/log"
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
	logFile, err := setupLogging(*logFilePath)
	if err != nil {
		log.Error().Err(err).Msg("Failed to open log file")
		return
	}
	defer logFile.Close()

	stop := make(chan os.Signal, 1)
	signal.Notify(stop, syscall.SIGINT, syscall.SIGTERM)

	for {
		config, err := readConfig(*configFile)
		if config.Retention.Enabled && !config.Retention.DryRun {
			if config.ProceedPackageVersion > config.Retention.VersionLimit {
				config.ProceedPackageVersion = config.Retention.VersionLimit
			}
		}

		log.Info().Msgf("Clean %s", *savePath)
		err = createDeleteDirectoryContents(*savePath)
		if err != nil {
			log.Error().Err(err).Msg("Error deleting directory contents")
		} else {
			log.Info().Msg("Directory contents deleted successfully")
		}

		if err != nil {
			log.Fatal().Err(err).Msg("Failed to read config")
		}

		go func() {
			<-stop
			log.Fatal().Msg("Received stop signal.")
		}()

		err = run()
		if err != nil {
			log.Error().Err(err).Msgf("Error syncing. Pause %d seconds", config.Timeout.IterationTimeout)
		}

		select {
		case <-stop:
			log.Info().Msg("Received stop signal, close loop.")
			return
		case <-time.After((time.Duration(config.Timeout.IterationTimeout) / 2) * time.Second):
			log.Info().Msgf("Starting new iteration after pause. %d seconds", config.Timeout.IterationTimeout)
		}
	}
}

func run() error {
	log.Info().Msg("Application start")

	config, err := readConfig(*configFile)
	if err != nil {
		log.Fatal().Err(err).Msg("Failed to read config")
	}

	ctx, cancel := context.WithTimeout(context.Background(), time.Duration(config.Timeout.SyncTimeout)*time.Second)
	defer cancel()

	log.Debug().Msgf("Chain sync loop start. Found %d chains", len(config.SyncChain))
	for _, chain := range config.SyncChain {
		log.Debug().Msg("Parsing url")
		_, err := url.ParseRequestURI(chain.Source.URL)
		if err != nil {
			log.Error().Err(err).Msg("Invalid source URI")
		}

		_, err = url.ParseRequestURI(chain.Destination.URL)
		if err != nil {
			log.Error().Err(err).Msg("Invalid dest URI")
		}

		select {
		case <-ctx.Done():
			log.Warn().Msgf("Timeout or cancel signal received, exiting run. Timeout: %d seconds", config.Timeout.SyncTimeout)
			return ctx.Err()
		default:
			sourcePackages, err := getPackages(ctx, chain.Source, config.Timeout)
			if err != nil {
				log.Error().Err(err).Msg("Failed to get packages from source")
				continue
			}

			destPackages, err := getPackages(ctx, chain.Destination, config.Timeout)
			if err != nil {
				log.Error().Err(err).Msg("Failed to get packages from destination")
				continue
			}

			syncPackages, err := getPackagesToSync(config, chain, sourcePackages, destPackages)
			if err != nil {
				log.Error().Err(err).Msg("Failed to SyncChain packages")
				continue
			}

			log.Debug().Msgf("syncPackage = %d", len(syncPackages))

			if len(syncPackages) > config.ProceedPackageLimit {
				syncPackages = syncPackages[:config.ProceedPackageLimit]
				newSourcePackages := make([]Package, len(syncPackages))
				copy(newSourcePackages, syncPackages)
				syncPackages = newSourcePackages
			}

			for i := range syncPackages {
				if len(syncPackages[i].Versions) > config.ProceedPackageVersion {
					syncPackages[i].Versions = syncPackages[i].Versions[:config.ProceedPackageVersion]
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
						err := downloadAndUploadPackage(ctx, config, chain, pkg, version, *savePath)
						if err != nil {
							errCh <- fmt.Errorf("failed to sync package %s/%s:%s, error: %w", pkg.Group, pkg.Name, version, err)
						}
					}(pkg, version)
				}
			}

			wg.Wait()
			close(errCh)

			for err := range errCh {
				return err
			}

			if config.Retention.Enabled && chain.Type != "asset" {
				log.Info().Str("feed", chain.Destination.Feed).Msgf("Start retention")
				destPackages, err = getPackages(ctx, chain.Destination, config.Timeout)
				if err != nil {
					log.Error().Err(err).Msg("Failed to get packages from destination")
				}
				err = retention(ctx, config, chain, destPackages)
				if err != nil {
					log.Error().Err(err).Msg("Retention failed")
				}
			}
		}
	}
	log.Info().Msgf("Pause %d seconds", config.Timeout.IterationTimeout)
	select {
	case <-ctx.Done():
		log.Warn().Msgf("Timeout or cancel signal received, exiting run. Timeout: %d seconds", config.Timeout.SyncTimeout)
		return ctx.Err()
	case <-time.After((time.Duration(config.Timeout.IterationTimeout) / 2) * time.Second):
		return nil
	}
}
