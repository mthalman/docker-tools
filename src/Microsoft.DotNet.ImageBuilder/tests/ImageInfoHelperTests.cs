﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using Microsoft.DotNet.ImageBuilder.Models.Image;
using Microsoft.DotNet.ImageBuilder.Models.Manifest;
using Microsoft.DotNet.ImageBuilder.ViewModel;
using Moq;
using Xunit;

namespace Microsoft.DotNet.ImageBuilder.Tests
{
    public class ImageInfoHelperTests
    {
        [Fact]
        public void ImageInfoHelper_MergeRepos_ImageDigest()
        {
            ImageInfo imageInfo1 = CreateImageInfo();

            ImageArtifactDetails imageArtifactDetails = new ImageArtifactDetails
            {
                Repos =
                {
                    new RepoData
                    {
                        Repo = "repo",
                        Images =
                        {
                            new ImageData
                            {
                                ManifestImage = imageInfo1,
                                Platforms =
                                {
                                    new PlatformData
                                    {
                                        Dockerfile = "image1",
                                        Digest = "digest"
                                    }
                                }
                            }
                        }
                    }
                }
            };

            ImageArtifactDetails targetImageArtifactDetails = new ImageArtifactDetails
            {
                Repos =
                {
                    new RepoData
                    {
                        Repo = "repo",
                        Images =
                        {
                            new ImageData
                            {
                                ManifestImage = imageInfo1,
                                Platforms =
                                {
                                    new PlatformData
                                    {
                                        Dockerfile = "image1"
                                    }
                                }
                            }
                        }
                    }
                }
            };

            ImageInfoHelper.MergeImageArtifactDetails(imageArtifactDetails, targetImageArtifactDetails);
            CompareImageArtifactDetails(imageArtifactDetails, targetImageArtifactDetails);
        }

        [Fact]
        public void ImageInfoHelper_MergeRepos_EmptyTarget()
        {
            ImageArtifactDetails imageArtifactDetails = new ImageArtifactDetails
            {
                Repos =
                {
                    new RepoData
                    {
                        Repo = "repo1",
                    },
                    new RepoData
                    {
                        Repo = "repo2",
                        Images =
                        {
                            new ImageData
                            {
                                Platforms =
                                {
                                    new PlatformData
                                    {
                                        Dockerfile = "image1"
                                    }
                                }
                            }
                        }
                    }
                }
            };

            ImageArtifactDetails targetImageArtifactDetails = new ImageArtifactDetails();
            ImageInfoHelper.MergeImageArtifactDetails(imageArtifactDetails, targetImageArtifactDetails);

            CompareImageArtifactDetails(imageArtifactDetails, targetImageArtifactDetails);
        }

        [Fact]
        public void ImageInfoHelper_MergeRepos_ExistingTarget()
        {
            PlatformData repo2Image1;
            PlatformData repo2Image2;
            PlatformData repo2Image3;
            PlatformData repo3Image1;

            DateTime oldCreatedDate = DateTime.Now.Subtract(TimeSpan.FromDays(1));
            DateTime newCreatedDate = DateTime.Now;

            ImageInfo imageInfo1 = CreateImageInfo();
            ImageInfo imageInfo2 = CreateImageInfo();

            ImageArtifactDetails imageArtifactDetails = new ImageArtifactDetails
            {
                Repos =
                {
                    new RepoData
                    {
                        Repo = "repo2",
                        Images =
                        {
                            new ImageData
                            {
                                ManifestImage = imageInfo1,
                                Platforms =
                                {
                                    {
                                        repo2Image1 = new PlatformData
                                        {
                                            Dockerfile = "image1",
                                            BaseImageDigest = "base1digest-NEW",
                                            Created = newCreatedDate
                                        }
                                    },
                                    {
                                        repo2Image3  = new PlatformData
                                        {
                                            Dockerfile = "image3"
                                        }
                                    }
                                }
                            }
                        }
                    },
                    new RepoData
                    {
                        Repo = "repo3",
                        Images =
                        {
                            new ImageData
                            {
                                ManifestImage = imageInfo2,
                                Platforms =
                                {
                                    {
                                        repo3Image1 = new PlatformData
                                        {
                                            Dockerfile = "image1"
                                        }
                                    }
                                }
                            }
                        }
                    },
                    new RepoData
                    {
                        Repo = "repo4",
                    }
                }
            };

            ImageArtifactDetails targetImageArtifactDetails = new ImageArtifactDetails
            {
                Repos =
                {
                    new RepoData
                    {
                        Repo = "repo1"
                    },
                    new RepoData
                    {
                        Repo = "repo2",
                        Images =
                        {
                            new ImageData
                            {
                                ManifestImage = imageInfo1,
                                Platforms =
                                {
                                    new PlatformData
                                    {
                                        Dockerfile = "image1",
                                        BaseImageDigest = "base1digest",
                                        Created = oldCreatedDate
                                    },
                                    {
                                        repo2Image2 = new PlatformData
                                        {
                                            Dockerfile = "image2",
                                            BaseImageDigest = "base2digest"
                                        }
                                    }
                                }
                            }
                        }
                    },
                    new RepoData
                    {
                        Repo = "repo3"
                    }
                }
            };

            ImageInfoHelper.MergeImageArtifactDetails(imageArtifactDetails, targetImageArtifactDetails);

            ImageArtifactDetails expected = new ImageArtifactDetails
            {
                Repos =
                {
                    new RepoData
                    {
                        Repo = "repo1"
                    },
                    new RepoData
                    {
                        Repo = "repo2",
                        Images =
                        {
                            new ImageData
                            {
                                Platforms =
                                {
                                    repo2Image1,
                                    repo2Image2,
                                    repo2Image3
                                }
                            }
                        }
                    },
                    new RepoData
                    {
                        Repo = "repo3",
                        Images =
                        {
                            new ImageData
                            {
                                Platforms =
                                {
                                    repo3Image1
                                }
                            }
                        }
                    },
                    new RepoData
                    {
                        Repo = "repo4",
                    }
                }
            };

            CompareImageArtifactDetails(expected, targetImageArtifactDetails);
        }

        /// <summary>
        /// Verifies that tags are merged between the source and destination.
        /// </summary>
        [Fact]
        public void ImageInfoHelper_MergeRepos_MergeTags()
        {
            PlatformData srcImage1;
            PlatformData targetImage2;

            ImageInfo imageInfo1 = CreateImageInfo();

            ImageArtifactDetails imageArtifactDetails = new ImageArtifactDetails
            {
                Repos =
                {
                    new RepoData
                    {
                        Repo = "repo1",
                        Images =
                        {
                            new ImageData
                            {
                                ManifestImage = imageInfo1,
                                Platforms =
                                {
                                    {
                                        srcImage1 = new PlatformData
                                        {
                                            Dockerfile = "image1",
                                            SimpleTags =
                                            {
                                                "tag1",
                                                "tag3"
                                            }
                                        }
                                    }
                                },
                                Manifest = new ManifestData
                                {
                                    SharedTags =
                                    {
                                        "shared1",
                                        "shared2"
                                    }
                                }
                            }
                        }
                    }
                }
            };

            ImageArtifactDetails targetImageArtifactDetails = new ImageArtifactDetails
            {
                Repos =
                {
                    new RepoData
                    {
                        Repo = "repo1",
                        Images =
                        {
                            new ImageData
                            {
                                ManifestImage = imageInfo1,
                                Platforms =
                                {
                                    new PlatformData
                                    {
                                        Dockerfile = "image1",
                                        SimpleTags =
                                        {
                                            "tag1",
                                            "tag2",
                                            "tag4"
                                        }
                                    },
                                    {
                                        targetImage2 = new PlatformData
                                        {
                                            Dockerfile = "image2",
                                            SimpleTags =
                                            {
                                                "a"
                                            }
                                        }
                                    }
                                },
                                Manifest = new ManifestData
                                {
                                    SharedTags =
                                    {
                                        "shared2",
                                        "shared3"
                                    }
                                }
                            }
                        }
                    }
                }
            };

            ImageInfoHelper.MergeImageArtifactDetails(imageArtifactDetails, targetImageArtifactDetails);

            ImageArtifactDetails expected = new ImageArtifactDetails
            {
                Repos =
                {
                    new RepoData
                    {
                        Repo = "repo1",
                        Images =
                        {
                            new ImageData
                            {
                                Platforms =
                                {
                                    new PlatformData
                                    {
                                        Dockerfile = "image1",
                                        SimpleTags =
                                        {
                                            "tag1",
                                            "tag2",
                                            "tag3",
                                            "tag4"
                                        }
                                    },
                                    targetImage2
                                },
                                Manifest = new ManifestData
                                {
                                    SharedTags =
                                    {
                                        "shared1",
                                        "shared2",
                                        "shared3"
                                    }
                                }
                            }
                        }
                    }
                }
            };

            CompareImageArtifactDetails(expected, targetImageArtifactDetails);
        }

        /// <summary>
        /// Verifies that tags are removed from existing images in the target
        /// if the same tag doesn't exist in the source. This handles cases where
        /// a shared tag has been moved from one image to another.
        /// </summary>
        [Fact]
        public void ImageInfoHelper_MergeRepos_RemoveTag()
        {
            PlatformData srcPlatform1;
            PlatformData targetPlatform2;

            ImageInfo imageInfo1 = CreateImageInfo();

            ImageArtifactDetails imageArtifactDetails = new ImageArtifactDetails
            {
                Repos =
                {
                    new RepoData
                    {
                        Repo = "repo1",
                        Images =
                        {
                            new ImageData
                            {
                                ManifestImage = imageInfo1,
                                Platforms =
                                {
                                    {
                                        srcPlatform1 = new PlatformData
                                        {
                                            Dockerfile = "image1",
                                            SimpleTags =
                                            {
                                                "tag1",
                                                "tag3"
                                            }
                                        }
                                    }
                                },
                                Manifest = new ManifestData
                                {
                                    SharedTags =
                                    {
                                        "sharedtag1",
                                    }
                                }
                            }
                        }
                    }
                }
            };

            ImageArtifactDetails targetImageArtifactDetails = new ImageArtifactDetails
            {
                Repos =
                {
                    new RepoData
                    {
                        Repo = "repo1",
                        Images =
                        {
                            new ImageData
                            {
                                ManifestImage = imageInfo1,
                                Platforms =
                                {
                                    {
                                        new PlatformData
                                        {
                                            Dockerfile = "image1",
                                            SimpleTags =
                                            {
                                                "tag1",
                                                "tag2",
                                                "tag4"
                                            }
                                        }
                                    },
                                    {
                                        targetPlatform2 = new PlatformData
                                        {
                                            Dockerfile = "image2",
                                            SimpleTags =
                                            {
                                                "a"
                                            }
                                        }
                                    }
                                },
                                Manifest = new ManifestData
                                {
                                    SharedTags =
                                    {
                                        "sharedtag2",
                                    }
                                }
                            }
                        }
                    }
                }
            };

            ImageInfoMergeOptions options = new ImageInfoMergeOptions
            {
                ReplaceTags = true
            };

            ImageInfoHelper.MergeImageArtifactDetails(imageArtifactDetails, targetImageArtifactDetails, options);

            ImageArtifactDetails expected = new ImageArtifactDetails
            {
                Repos =
                {
                    new RepoData
                    {
                        Repo = "repo1",
                        Images =
                        {
                            new ImageData
                            {
                                Platforms =
                                {
                                    srcPlatform1,
                                    targetPlatform2
                                },
                                Manifest = new ManifestData
                                {
                                    SharedTags =
                                    {
                                        "sharedtag1",
                                    }
                                }
                            }
                        }
                    }
                }
            };

            CompareImageArtifactDetails(expected, targetImageArtifactDetails);
        }

        public static void CompareImageArtifactDetails(ImageArtifactDetails expected, ImageArtifactDetails actual)
        {
            Assert.Equal(JsonHelper.SerializeObject(expected), JsonHelper.SerializeObject(actual));
        }

        private static ImageInfo CreateImageInfo()
        {
            return ImageInfo.Create(
                new Image
                {
                    Platforms = Array.Empty<Platform>()
                },
                "fullrepo",
                "repo",
                new ManifestFilter(),
                new VariableHelper(new Manifest(), Mock.Of<IManifestOptionsInfo>(), null),
                "base");
        }
    }
}
