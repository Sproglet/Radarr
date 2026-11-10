import ModelBase from 'App/ModelBase';
import Language from 'Language/Language';
import Movie from 'Movie/Movie';
import { QualityModel } from 'Quality/Quality';
import CustomFormat from 'typings/CustomFormat';
import Rejection from 'typings/Rejection';

export interface InteractiveImportCommandOptions {
  path: string;
  folderName: string;
  movieId: number;
  releaseGroup?: string;
  quality: QualityModel;
  languages: Language[];
  indexerFlags: number;
  downloadId?: string;
  downloadClientId?: number;
  movieEditionId?: number;
  movieFileId?: number;
}

interface InteractiveImport extends ModelBase {
  path: string;
  relativePath: string;
  folderName: string;
  name: string;
  size: number;
  releaseGroup: string;
  quality: QualityModel;
  languages: Language[];
  movie?: Movie;
  qualityWeight: number;
  customFormats: CustomFormat[];
  indexerFlags: number;
  rejections: Rejection[];
  movieFileId?: number;
  movieEditionId?: number;
  movieEditionName?: string;
  editionAssignmentValid?: boolean;
  downloadId?: string;
  downloadClientId?: number;
}

export default InteractiveImport;
