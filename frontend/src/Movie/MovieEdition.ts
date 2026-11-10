import ModelBase from 'App/ModelBase';
import { QualityModel } from 'Quality/Quality';

interface MovieEdition extends ModelBase {
  movieId: number;
  editionKey: string;
  name: string;
  isPrimary: boolean;
  monitored: boolean;
  effectiveMonitored: boolean;
  qualityProfileId: number;
  qualityProfileName?: string;
  movieFileId?: number;
  relativePath?: string;
  quality?: QualityModel;
  attributes: Record<string, string>;
  matchRules: string[];
  ruleRevision: number;
  state: 'present' | 'missing' | 'invalid';
  rejectionReason?: string;
}

export default MovieEdition;
