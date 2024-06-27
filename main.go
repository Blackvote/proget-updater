package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"github.com/rs/zerolog/log"
	"gopkg.in/yaml.v2"
	"io"
	"io/ioutil"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"strings"
	"sync"
	"syscall"
	"time"
)

type Config struct {
	SyncChain           []SyncChain     `yaml:"syncChain"`
	Timeout             TimeoutConfig   `yaml:"timeout"`
	ProceedPackageLimit int             `yaml:"proceedPackageLimit"`
	Retention           RetentionConfig `yaml:"retention"`
}

type SyncChain struct {
	Source      ProgetConfig `yaml:"source"`
	Destination ProgetConfig `yaml:"destination"`
}

type ProgetConfig struct {
	URL    string `yaml:"url"`
	APIKey string `yaml:"apiKey"`
	Feed   string `yaml:"feed"`
}

type Package struct {
	Group    string   `yaml:"group"`
	Name     string   `yaml:"name"`
	Versions []string `yaml:"versions"`
}

type TimeoutConfig struct {
	WebRequestTimeout int `yaml:"webRequestTimeout"`
	IterationTimeout  int `yaml:"iterationTimeout"`
	SyncTimeout       int `yaml:"syncTimeout"`
}

type RetentionConfig struct {
	Enabled      bool `yaml:"enabled"`
	DryRun       bool `yaml:"dry-run"`
	VersionLimit int  `yaml:"versionLimit"`
}

var (
	configFile  *string = new(string)
	savePath    *string = new(string)
	logFilePath *string = new(string)
)

const maxRetries = 5

func init() {
	flag.StringVar(configFile, "c", "config.yml", "path to config file")
	flag.StringVar(savePath, "p", "./packages", "path to save downloaded packages")
	flag.StringVar(logFilePath, "l", "", "path to logfile")
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
	err := deleteDirectoryContents(*savePath)
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

	log.Info().Time("timestamp", time.Now()).Msg("Application start")

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
				log.Fatal().Err(err).Msg("Failed to get packages from source")
			}

			destPackages, err := getPackages(parentCtx, chain.Destination, config.Timeout)
			if err != nil {
				log.Fatal().Err(err).Msg("Failed to get packages from destination")
			}

			err = SyncPackages(parentCtx, config, chain, sourcePackages, destPackages, *savePath)
			if err != nil {
				log.Fatal().Err(err).Msg("Failed to SyncChain packages")
			}

			if config.Retention.Enabled {
				log.Info().Msgf("Start retention")
				destPackages, err = getPackages(parentCtx, chain.Destination, config.Timeout)
				if err != nil {
					log.Fatal().Err(err).Msg("Failed to get packages from destination")
				}

				err = retention(parentCtx, config, chain, destPackages)
				if err != nil {
					log.Fatal().Err(err).Msg("Failed to retention package")
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

func readConfig(configFile string) (*Config, error) {
	data, err := ioutil.ReadFile(configFile)
	if err != nil {
		return nil, err
	}

	var config Config
	err = yaml.Unmarshal(data, &config)
	if err != nil {
		return nil, err
	}

	return &config, nil
}

func setupLogging(logFilePath string) (*os.File, error) {
	logFile, err := os.OpenFile(logFilePath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0666)
	if err != nil {
		return nil, err
	}

	multiWriter := io.MultiWriter(os.Stdout, logFile)
	log.Logger = log.Output(multiWriter).With().
		Str("app", "Updater").
		Logger()
	return logFile, nil
}

func deleteDirectoryContents(dir string) error {
	files, err := ioutil.ReadDir(dir)
	if err != nil {
		return err
	}

	for _, file := range files {
		filePath := filepath.Join(dir, file.Name())
		if file.IsDir() {
			err = os.RemoveAll(filePath)
		} else {
			err = os.Remove(filePath)
		}
		if err != nil {
			return err
		}
	}

	return nil
}

func downloadAndUploadPackage(ctx context.Context, config *Config, chain SyncChain, pkg Package, version, savePath string) error {
	downloadURL := fmt.Sprintf("%s/upack/%s/download/%s/%s/%s", chain.Source.URL, chain.Source.Feed, pkg.Group, pkg.Name, version)
	uploadURL := fmt.Sprintf("%s/upack/%s/upload", chain.Destination.URL, chain.Destination.Feed)

	err := os.MkdirAll(savePath, os.ModePerm)
	if err != nil {
		log.Error().Msgf("Failed to create dir %s", savePath)
	}

	filePath := filepath.Join(savePath, fmt.Sprintf("%s.%s.upack", pkg.Name, version))

	err = downloadFile(ctx, downloadURL, chain.Source.APIKey, filePath, config.Timeout)
	if err != nil {
		return err
	}

	return uploadFile(ctx, uploadURL, chain.Destination.APIKey, filePath, config.Timeout)
}

func getPackages(ctx context.Context, progetConfig ProgetConfig, timeoutConfig TimeoutConfig) ([]Package, error) {
	url := fmt.Sprintf("%s/upack/%s/packages", progetConfig.URL, progetConfig.Feed)
	req, err := http.NewRequestWithContext(ctx, "GET", url, nil)
	if err != nil {
		return nil, err
	}

	req.Header.Set("X-ApiKey", progetConfig.APIKey)
	client := &http.Client{
		Timeout: time.Duration(timeoutConfig.IterationTimeout) * time.Second,
	}

	var resp *http.Response
	var packages []Package

	for attempt := 1; attempt <= maxRetries; attempt++ {
		log.Info().Str("url", url).Msgf("Attempt %d to get package list", attempt)
		resp, err = client.Do(req)
		if err == nil && resp.StatusCode == http.StatusOK {
			err = json.NewDecoder(resp.Body).Decode(&packages)
			resp.Body.Close()
			if err != nil {
				log.Error().Str("url", url).Msgf("Attempt %d failed to decode answer. Error: %s", attempt, err)
				time.Sleep(3 * time.Duration(attempt) * time.Second)
				continue
			}

			log.Info().Str("url", url).Msgf("Package count in %s/%s: %d", progetConfig.URL, progetConfig.Feed, len(packages))

			var packageList strings.Builder
			for _, pkg := range packages {
				packageList.WriteString(fmt.Sprintf("%s/%s: %s | ", pkg.Group, pkg.Name, strings.Join(pkg.Versions, " ")))
			}
			log.Info().Str("url", url).Msg(packageList.String())

			if err != nil {
				log.Error().Str("url", url).Msgf("Failed to get package attempt %d, Error: %s", attempt, err)
			}
			return packages, nil
		}
		resp.Body.Close()
		if resp != nil && err == nil {
			log.Error().Str("url", url).Msgf("Failed to get package attempt %d, Status: %s", attempt, resp.Status)
		}
		time.Sleep(3 * time.Duration(attempt) * time.Second)
	}
	return nil, err
}

func SyncPackages(ctx context.Context, config *Config, chain SyncChain, sourcePackages, destPackages []Package, savePath string) error {
	sourcePackageMap := make(map[string]map[string]bool)
	for _, pkg := range sourcePackages {
		key := fmt.Sprintf("%s:%s", pkg.Group, pkg.Name)
		if sourcePackageMap[key] == nil {
			sourcePackageMap[key] = make(map[string]bool)
		}
		for i, version := range pkg.Versions {
			if !config.Retention.Enabled {
				sourcePackageMap[key][version] = true
			} else {
				if i <= config.Retention.VersionLimit-1 {
					sourcePackageMap[key][version] = true
				} else {
					if !config.Retention.DryRun {
						sourcePackageMap[key][version] = false
					} else {
						log.Warn().Str("url", chain.Destination.URL).Msgf("%s:%s exceedes version limit, will be processed (dry-run is on)", key, version)
						sourcePackageMap[key][version] = true
					}
				}
			}
		}
	}

	destPackageMap := make(map[string]map[string]bool)
	for _, pkg := range destPackages {
		key := fmt.Sprintf("%s:%s", pkg.Group, pkg.Name)
		if destPackageMap[key] == nil {
			destPackageMap[key] = make(map[string]bool)
		}
		for _, version := range pkg.Versions {
			destPackageMap[key][version] = true
		}
	}

	if len(sourcePackageMap) < config.ProceedPackageLimit {
		config.ProceedPackageLimit = len(sourcePackageMap)
	}
	semaphore := make(chan struct{}, config.ProceedPackageLimit)
	var wg sync.WaitGroup
	for _, pkg := range sourcePackages {
		for _, version := range pkg.Versions {
			key := fmt.Sprintf("%s:%s", pkg.Group, pkg.Name)
			if !destPackageMap[key][version] {
				if sourcePackageMap[key][version] {
					log.Info().Str("url", chain.Destination.URL).Msgf("%s:%s:%s not found. Syncing", pkg.Group, pkg.Name, version)
					wg.Add(1)
					go func(pkg Package, version string) {
						defer wg.Done()
						semaphore <- struct{}{}
						defer func() { <-semaphore }()
						err := downloadAndUploadPackage(ctx, config, chain, pkg, version, savePath)
						if err != nil {
							log.Error().Err(err).Str("url", chain.Destination.URL).Msgf("Failed to Sync package %s:%s", pkg.Name, version)
						}
					}(pkg, version)
				}
			} else {
				log.Info().Str("url", chain.Destination.URL).Msgf("%s:%s:%s found.", pkg.Group, pkg.Name, version)
			}
		}
	}
	wg.Wait()
	return nil
}

func downloadFile(ctx context.Context, url, apiKey, filePath string, timeoutConfig TimeoutConfig) error {
	log.Info().Str("url", url).Msgf("Download package %s", filePath)

	req, err := http.NewRequestWithContext(ctx, "GET", url, nil)
	if err != nil {
		return err
	}
	req.Header.Set("X-ApiKey", apiKey)
	client := &http.Client{
		Timeout: time.Duration(timeoutConfig.WebRequestTimeout) * time.Second,
	}

	for attempt := 1; attempt <= maxRetries; attempt++ {
		log.Info().Str("url", url).Msgf("Attempt download %d file %s", attempt, filePath)

		resp, err := client.Do(req)
		if err == nil && resp.StatusCode == http.StatusOK {
			out, err := os.Create(filePath)

			if err != nil {
				log.Error().Err(err).Str("url", url).Msgf("Failed to create file")
				time.Sleep(3 * time.Duration(attempt) * time.Second)
				continue
			}

			fileSize, err := io.Copy(out, resp.Body)
			if err != nil {
				log.Error().Err(err).Str("url", url).Msgf("Failed to copy response body")
			}

			resp.Body.Close()
			out.Close()
			fileSizeMB := float64(fileSize) / (1024 * 1024)
			log.Info().Str("url", url).Msgf("%s file Size: %.2f MB", strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"), fileSizeMB)
			log.Info().Str("url", url).Msgf("Success download %s", strings.TrimPrefix(filePath, "packages\\"))
			return nil
		}

		if resp != nil {
			log.Error().Str("url", url).Msgf("Failed download attempt %d, Status: %s", attempt, resp.Status)
		}

		if err != nil {
			log.Error().Str("url", url).Msgf("Failed download attempt %d for file %s, Error: %s", attempt, strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"), err)
		}
		resp.Body.Close()
		time.Sleep(3 * time.Duration(attempt) * time.Second)
	}

	return fmt.Errorf("failed to retrieve %s after %d attempts", url, maxRetries)
}

func uploadFile(ctx context.Context, url, apiKey, filePath string, timeoutConfig TimeoutConfig) error {
	log.Info().Str("url", url).Msgf("Upload package %s", strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"))
	file, err := os.Open(filePath)
	if err != nil {
		return err
	}
	defer file.Close()

	req, err := http.NewRequestWithContext(ctx, "POST", url, file)
	if err != nil {
		return err
	}
	req.Header.Set("X-ApiKey", apiKey)
	client := &http.Client{
		Timeout: time.Duration(timeoutConfig.WebRequestTimeout) * time.Second,
	}

	for attempt := 1; attempt <= maxRetries; attempt++ {
		log.Info().Str("url", url).Msgf("Attempt %d upload for file %s", attempt, strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"))

		resp, err := client.Do(req)
		resp.Body.Close()
		if err == nil && resp.StatusCode == http.StatusCreated {
			log.Info().Str("url", url).Msgf("Success upload: for file %s", strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"))
			err = os.Remove(filePath)
			return nil
		}

		if resp != nil {
			log.Error().Str("url", url).Msgf("Failed upload attempt %d for file %s, Status: %s", attempt, strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"), resp.Status)
		}

		if err != nil {
			log.Error().Str("url", url).Msgf("Failed upload attempt %d for file %s, Error: %s", attempt, strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"), err)
		}

		time.Sleep(3 * time.Duration(attempt) * time.Second)
	}
	return err
}

func retention(ctx context.Context, config *Config, chain SyncChain, packages []Package) error {
	for _, pkg := range packages {
		if len(pkg.Versions) <= config.Retention.VersionLimit {
			log.Info().Msgf("Version limit less than version, skip retention")
			continue
		}

		for i, version := range pkg.Versions {
			if i > config.Retention.VersionLimit-1 {
				deleteURL := fmt.Sprintf("%s/upack/%s/delete/%s/%s/%s", chain.Destination.URL, chain.Destination.Feed, pkg.Group, pkg.Name, version)
				log.Info().Str("url", deleteURL).Msgf("Package: %s/%s:%s mark for delete", pkg.Group, pkg.Name, version)
				if !config.Retention.DryRun {
					req, err := http.NewRequestWithContext(ctx, "POST", deleteURL, nil)
					if err != nil {
						log.Error().Str("url", deleteURL).Msgf("Failed to create reqeust for %s", deleteURL)
						continue
					}

					req.Header.Set("X-ApiKey", chain.Destination.APIKey)
					client := &http.Client{}

					for attempt := 1; attempt <= maxRetries; attempt++ {
						log.Info().Str("url", deleteURL).Msgf("Attempt %d to delete %s/%s:%s", attempt, pkg.Group, pkg.Name, version)

						resp, err := client.Do(req)
						if err == nil && resp.StatusCode == http.StatusOK {
							resp.Body.Close()
							log.Info().Str("url", deleteURL).Msgf("Success delete %s/%s:%s", pkg.Group, pkg.Name, version)
							break
						}

						if resp != nil {
							log.Error().Str("url", deleteURL).Msgf("Attempt %d failed to delete %s/%s:%s. Status: %s", attempt, pkg.Group, pkg.Name, version, resp.Status)
							resp.Body.Close()
						}
						time.Sleep(3 * time.Duration(attempt) * time.Second)
					}
				} else {
					log.Info().Str("url", deleteURL).Msgf("Skip delete: %s/%s:%s (dry-run is on)", pkg.Group, pkg.Name, version)
				}
			}
		}

	}
	return nil
}
