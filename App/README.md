# Custom Vision Import

Small utility to upload exports from [LabelBox](https://www.labelbox.com/) into [Azure Custom Vision service](https://azure.microsoft.com/en-us/services/cognitive-services/custom-vision-service/).

## How to use

The console application is configuration-driven.

Open **appsettings.json** and update fields:

* `bboxFormat` - either `labelbox` or `cntk`
* `labelBoxFile` - path to the JSON file exported from LabelBox
* `imagesPath` - where images are or will be downloaded
* `imageExtension` - what is the image extension (probably jpg or png)
* `visionProjectId` - if you already have a Custom Vision Project, otherwise empty
* `trainingKey` - Custom Vision key
* `endpoint` - Custom Vision endpoint (there's only one currently)
* `projectName` - name of the newly created project in Custom Vision
* `tagCleanup` - whether to remove tags with less than 15 images (required for training)
* `online` - whether working online (with the Vision service) or just preparing data without pushing to the service
* `dumpResults` - whether to create *dump.txt* with all information that was uploaded to the service (for debugging)