﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BeatSaberMarkupLanguage.Components;
using BeatSaverUpdater.Migration;
using HMUI;
using IPA.Utilities;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using VRUIControls;
using Zenject;

namespace BeatSaverUpdater.UI
{
    internal class UpdateButton : IInitializable, IDisposable
    {
        private ClickableImage? image;
        private CancellationTokenSource? tokenSource;
        private string? oldLevelHash;
        private string? downloadedLevelHash;

        private readonly DiContainer container;
        private readonly HoverHintController hoverHintController;
        private readonly SelectLevelCategoryViewController selectLevelCategoryViewController;
        private readonly IconSegmentedControl levelCategorySegmentedControl;
        private readonly LevelCollectionNavigationController levelCollectionNavigationController;
        private readonly StandardLevelDetailViewController standardLevelDetailViewController;
        private readonly PopupModal popupModal;
        private readonly List<IMigrator> migrators;
        private SongDetailsWrapper? songDetailsWrapper;

        public UpdateButton(DiContainer container, HoverHintController hoverHintController, SelectLevelCategoryViewController selectLevelCategoryViewController,
            LevelCollectionNavigationController levelCollectionNavigationController, StandardLevelDetailViewController standardLevelDetailViewController,
            PopupModal popupModal, List<IMigrator> migrators, [InjectOptional] SongDetailsWrapper? songDetailsWrapper)
        {
            this.container = container;
            this.hoverHintController = hoverHintController;
            this.selectLevelCategoryViewController = selectLevelCategoryViewController;
            levelCategorySegmentedControl = selectLevelCategoryViewController.GetField<IconSegmentedControl, SelectLevelCategoryViewController>("_levelFilterCategoryIconSegmentedControl");
            this.levelCollectionNavigationController = levelCollectionNavigationController;
            this.standardLevelDetailViewController = standardLevelDetailViewController;
            this.popupModal = popupModal;
            this.migrators = migrators;
            this.songDetailsWrapper = songDetailsWrapper;
        }

        public void Initialize()
        {
            _ = InitializeAsync();

            standardLevelDetailViewController.didChangeContentEvent += ContentChanged;
        }

        public void Dispose()
        {
            if (image != null)
            {
                image.OnClickEvent -= Clicked;
            }
        }

        private async Task InitializeAsync()
        {
            image = CreateImage();
            using var mrs = Plugin.Metadata.Assembly.GetManifestResourceStream("BeatSaverUpdater.Images.Logo.png");
            using var ms = new MemoryStream();
            if (mrs != null)
            {
                await mrs.CopyToAsync(ms);
            }

            image.OnClickEvent += Clicked;
            image.sprite = await BeatSaberMarkupLanguage.Utilities.LoadSpriteAsync(ms.ToArray());
            image.sprite.texture.wrapMode = TextureWrapMode.Clamp;
            image.gameObject.SetActive(false);
        }

        private ClickableImage CreateImage()
        {
            var gameObject = new GameObject("BeatSaverUpdater");
            var image = gameObject.AddComponent<ClickableImage>();
            image.material = BeatSaberMarkupLanguage.Utilities.ImageResources.NoGlowMat;

            image.rectTransform.SetParent(standardLevelDetailViewController.transform);
            image.rectTransform.localPosition = new Vector3(32f, 32f, 0f);
            image.rectTransform.localScale = new Vector3(.3f, .3f, .3f);
            image.rectTransform.sizeDelta = new Vector2(20f, 20f);
            gameObject.AddComponent<LayoutElement>();

            var canvas = gameObject.AddComponent<Canvas>();
            var additionalShaderChannels = canvas.additionalShaderChannels;
            additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord1;
            additionalShaderChannels |= AdditionalCanvasShaderChannels.TexCoord2;
            additionalShaderChannels |= AdditionalCanvasShaderChannels.Tangent;
            additionalShaderChannels |= AdditionalCanvasShaderChannels.Normal;
            canvas.additionalShaderChannels = additionalShaderChannels;
            container.InstantiateComponent<VRGraphicRaycaster>(gameObject);

            var hoverHint = image.gameObject.AddComponent<HoverHint>();
            hoverHint.SetField("_hoverHintController", hoverHintController);
            hoverHint.text = "Update Map!";

            return image;
        }

        private IEnumerator SetActive(bool value)
        {
            yield return null;
            image?.gameObject.SetActive(value);
        }

        private void ContentChanged(StandardLevelDetailViewController standardLevelDetailViewController, StandardLevelDetailViewController.ContentType contentType)
        {
            if (contentType == StandardLevelDetailViewController.ContentType.OwnedAndReady)
            {
                _ = Task.Run(() => BeatmapSelected(standardLevelDetailViewController.beatmapLevel));
            }
        }

        private async Task BeatmapSelected(IPreviewBeatmapLevel beatmapLevel)
        {
            tokenSource?.Cancel();
            tokenSource?.Dispose();
            tokenSource = new CancellationTokenSource();

            if (image != null)
            {
                if (beatmapLevel is CustomPreviewBeatmapLevel customPreviewBeatmapLevel && !customPreviewBeatmapLevel.levelID.EndsWith(" WIP"))
                {
                    if (!PluginConfig.Instance.UseCache || songDetailsWrapper == null || !await songDetailsWrapper.SongExists(customPreviewBeatmapLevel.GetBeatmapHash()))
                    {
                        var needsUpdate = await customPreviewBeatmapLevel.NeedsUpdate(tokenSource.Token);
                        standardLevelDetailViewController.StartCoroutine(SetActive(needsUpdate));
                        return;
                    }
                }
                standardLevelDetailViewController.StartCoroutine(SetActive(false));
            }
        }

        private async void Clicked(PointerEventData _)
        {
            if (standardLevelDetailViewController.beatmapLevel is CustomPreviewBeatmapLevel beatmapLevel)
            {
                var newHash = (await beatmapLevel.GetBeatSaverBeatmap(CancellationToken.None))?.LatestVersion.Hash;

                if (newHash != null)
                {
                    var newLevel = SongCore.Loader.GetLevelByHash(newHash ?? "");
                    if (newLevel != null)
                    {
                        popupModal.ShowYesNoModal("Updated map already exists!", () =>
                        {
                            if (popupModal.CheckboxValue)
                            {
                                downloadedLevelHash = newHash;
                                UpdateReferences(beatmapLevel, newLevel);
                            }
                            else
                            {
                                popupModal.HideModal();
                                OpenMap(newLevel);
                            }
                        }, "Open Map", "Dismiss", showCheckbox: true, referencesActive: true);
                        return;
                    }
                }

                popupModal.ShowYesNoModal("This map has an update on BeatSaver. Do you want to download it?", () => UpdateRequested(beatmapLevel));
            }
        }

        private async void UpdateRequested(CustomPreviewBeatmapLevel beatmapLevel)
        {
            tokenSource?.Cancel();
            tokenSource = new CancellationTokenSource();
            popupModal.ShowDownloadingModal("Updating map", () => tokenSource.Cancel());
            oldLevelHash = beatmapLevel.GetBeatmapHash();
            downloadedLevelHash = await beatmapLevel.UpdateBeatmap(tokenSource.Token, popupModal);
            if (downloadedLevelHash != null)
            {
                SongCore.Loader.SongsLoadedEvent += OnSongsLoaded;
                SongCore.Loader.Instance.RefreshSongs(false);
            }
        }

        private void OpenMap(CustomPreviewBeatmapLevel beatmapLevel)
        {
            levelCategorySegmentedControl.SelectCellWithNumber(3);
            levelCollectionNavigationController.SelectLevel(beatmapLevel);
        }

        private void OnSongsLoaded(SongCore.Loader _, System.Collections.Concurrent.ConcurrentDictionary<string, CustomPreviewBeatmapLevel> __)
        {
            SongCore.Loader.SongsLoadedEvent -= OnSongsLoaded;
            var oldLevel = SongCore.Loader.GetLevelByHash(oldLevelHash ?? "");
            var downloadedLevel = SongCore.Loader.GetLevelByHash(downloadedLevelHash ?? "");
            if (downloadedLevel != null)
            {
                OpenMap(downloadedLevel);
                popupModal.ShowYesNoModal("Map Updated!\nWould you also like to update all of its references?", () => UpdateReferences(oldLevel, downloadedLevel), "Update", "Dismiss", true);
            }
            else
            {
                popupModal.HideModal();
            }
        }

        private async void UpdateReferences(CustomPreviewBeatmapLevel? oldLevel, CustomPreviewBeatmapLevel downloadedLevel)
        {
            if (oldLevel != null)
            {
                popupModal.ShowLoadingModal("Migrating References");
                await Task.Run(() => UpdateReferencesAsync(oldLevel, downloadedLevel));
                var downloadedLevelAfterUpdate = SongCore.Loader.GetLevelByHash(downloadedLevelHash ?? "");
                if (downloadedLevelAfterUpdate != null)
                {
                    OpenMap(downloadedLevel);
                }
            }
            popupModal.HideModal();
        }

        private void UpdateReferencesAsync(CustomPreviewBeatmapLevel oldLevel, CustomPreviewBeatmapLevel downloadedLevel)
        {
            var preventDelete = false;

            foreach (var migrator in migrators)
            {
                preventDelete = preventDelete || migrator.MigrateMap(oldLevel, downloadedLevel);
            }

            if (!preventDelete)
            {
                SongCore.Loader.Instance.DeleteSong(oldLevel.customLevelPath);
            }
        }
    }
}
